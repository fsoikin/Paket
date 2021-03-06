namespace FSharp.Polyfill

open System.Threading

type VolatileBarrier() =
    [<VolatileField>]
    let mutable isStopped = false
    member __.Proceed = not isStopped
    member __.Stop() = isStopped <- true

/// Extensions for async workflows.
[<AutoOpen>]
module AsyncExtensions = 
  open System
  open System.Threading.Tasks
  open System.Threading

  type Microsoft.FSharp.Control.Async with 
     /// Runs both computations in parallel and returns the result as a tuple.
    static member Parallel (a : Async<'a>, b : Async<'b>) : Async<'a * 'b> =
        async {
            let! a' = Async.StartChild a
            let! b' = Async.StartChild b
            let! a'' = a'
            let! b'' = b'
            return (a'',b'')
        }
    static member awaitTaskWithToken (fallBack:unit -> 'T) (item: Task<'T>) : Async<'T> =
        async {
            let! ct = Async.CancellationToken
            return! Async.FromContinuations(fun (success, error, cancel) ->
                async {
                    let l = obj()
                    let mutable finished = false
                    let whenFinished f =
                        let isJustFinished =
                            if finished then false
                            else
                                lock l (fun () ->
                                    if finished then
                                        false
                                    else
                                        finished <- true
                                        true
                                )
                        if isJustFinished then
                            f()
                    use! reg = Async.OnCancel(fun () ->
                        whenFinished (fun () ->
                            try let result = fallBack()
                                success result
                            with e -> error e))
                    item.ContinueWith (fun (t:Task<'T>) ->
                        whenFinished (fun () ->
                            if t.IsCanceled then
                                cancel (OperationCanceledException("The underlying task has been cancelled"))
                            elif t.IsFaulted then
                                if t.Exception.InnerExceptions.Count = 1 then
                                    error t.Exception.InnerExceptions.[0]
                                else
                                    error t.Exception
                            else success t.Result))
                        |> ignore
                } |> fun a -> Async.Start(a, ct)
            )
        }

    static member StartCatchCancellation(work, ?cancellationToken) =
        Async.FromContinuations(fun (cont, econt, _) ->
          // When the child is cancelled, report OperationCancelled
          // as an ordinary exception to "error continuation" rather
          // than using "cancellation continuation"
          let ccont e = econt e
          // Start the workflow using a provided cancellation token
          Async.StartWithContinuations( work, cont, econt, ccont,
                                        ?cancellationToken=cancellationToken) )

    /// Like StartAsTask but gives the computation time to so some regular cancellation work
    static member StartAsTaskProperCancel (computation : Async<_>, ?taskCreationOptions, ?cancellationToken:CancellationToken) : Task<_> =
        let token = defaultArg cancellationToken Async.DefaultCancellationToken
        let taskCreationOptions = defaultArg taskCreationOptions TaskCreationOptions.None
        let tcs = new TaskCompletionSource<_>(taskCreationOptions)

        let a =
            async {
                try
                    // To ensure we don't cancel this very async (which is required to properly forward the error condition)
                    let! result = Async.StartCatchCancellation(computation, token)
                    do
                        tcs.SetResult(result)
                with exn ->
                    tcs.SetException(exn)
            }
        Async.Start(a)
        tcs.Task

    static member map f a =
        async { return f a }
    static member tryFind (f : 'T -> bool) (tasks : Async<'T> seq) = async {
         match Seq.toArray tasks with
         | [||] -> return [||], None
         | [|t|] ->
            let! res = t
            let task = Task.FromResult res
            return if f res then [|task|], Some 0 else [|task|], None
         | tasks ->
         let! t = Async.CancellationToken
         return! Async.FromContinuations <|
             fun (sc,ec,cc) ->
                 let currentIndex = ref 0
                 let exnCount = ref 0
                 let innerCts = CancellationTokenSource.CreateLinkedTokenSource t
                 let results = Array.init tasks.Length (fun _ -> TaskCompletionSource<'T>())
                 let retResults = results |> Array.map (fun tcs -> tcs.Task)

                 let scont index (result : 'T) =
                     results.[index].TrySetResult result |> ignore
                     match f result with
                     | true when Interlocked.Increment exnCount = 1 ->
                        innerCts.Cancel()
                        sc (retResults, (Some index))
                     | false when Interlocked.Increment currentIndex = tasks.Length ->
                        sc (retResults,None)
                     | _ -> ()
    
                 let econt index (exn : exn) =
                     results.[index].TrySetException exn |> ignore
                     if Interlocked.Increment exnCount = 1 then 
                         innerCts.Cancel() ; ec exn
    
                 let ccont index (exn : OperationCanceledException) =
                     results.[index].TrySetCanceled () |> ignore
                     if Interlocked.Increment exnCount = 1 then
                         innerCts.Cancel(); cc exn
    
                 for i, task in tasks |> Seq.mapi (fun i t -> i, t) do
                     ignore <| System.Threading.Tasks.Task.Factory.StartNew(fun () -> 
                        Async.StartWithContinuations(task, scont i, econt i, ccont i, innerCts.Token))
     }
