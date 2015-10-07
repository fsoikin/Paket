﻿module Paket.ProjectFile.InterProjectDependencySpecs

open Paket
open NUnit.Framework
open FsUnit
open System

[<Test>]
let ``should detect no dependencies in empty proj file``() =
    ProjectFile.Load("./ProjectFile/TestData/Empty.fsprojtest").Value.GetInterProjectDependencies()
    |> shouldBeEmpty

[<Test>]
let ``should detect Paket dependency in Project1 proj file``() =
    ProjectFile.Load("./ProjectFile/TestData/Project1.fsprojtest").Value.GetInterProjectDependencies()
    |> List.map (fun p -> p.Name)
    |> shouldEqual ["Paket"]

[<Test>]
let ``should detect Paket and Paket.Core dependency in Project2 proj file``() =
    ProjectFile.Load("./ProjectFile/TestData/Project2.fsprojtest").Value.GetInterProjectDependencies()
    |> List.map (fun p -> p.Name)
    |> shouldEqual ["Paket"; "Paket.Core"]

[<Test>]
let ``should detect path for dependencies in Project2 proj file``() =
    let paths =
        ProjectFile.Load("./ProjectFile/TestData/Project2.fsprojtest").Value.GetInterProjectDependencies()
        |> List.map (fun p -> normalizePath p.Path)

    paths.[0].EndsWith(normalizePath "src/Paket/Paket.fsproj") |> shouldEqual true
    paths.[1].EndsWith(normalizePath "Paket.Core/Paket.Core.fsproj") |> shouldEqual true

[<Test>]
let ``should detect Guids for dependencies in Project2 proj file``() =
    let p = ProjectFile.Load("./ProjectFile/TestData/Project2.fsprojtest").Value
    p.GetProjectGuid() |> shouldEqual (Guid.Parse "e789c72a-5cfd-436b-8ef1-61aa2852a89f")
    p.GetInterProjectDependencies()
    |> List.map (fun p -> p.GUID.ToString())
    |> shouldEqual ["09b32f18-0c20-4489-8c83-5106d5c04c93"; "7bab0ae2-089f-4761-b138-a717aa2f86c5"]