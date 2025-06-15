namespace FSharp.Analyzers.SDK

#nowarn "57"

open System
open Microsoft.Extensions.Logging
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.EditorServices
open System.Reflection
open System.Runtime.InteropServices
open FSharp.Compiler.Text

module EntityCache =
    let private entityCache = EntityCache()

    let getEntities (publicOnly: bool) (checkFileResults: FSharpCheckFileResults) =
        try
            let res =
                [
                    yield!
                        AssemblyContent.GetAssemblySignatureContent
                            AssemblyContentType.Full
                            checkFileResults.PartialAssemblySignature
                    let ctx = checkFileResults.ProjectContext

                    let assembliesByFileName =
                        ctx.GetReferencedAssemblies()
                        |> Seq.groupBy (fun asm -> asm.FileName)
                        |> Seq.map (fun (fileName, asms) -> fileName, List.ofSeq asms)
                        |> Seq.toList
                        |> List.rev // if mscorlib.dll is the first then FSC raises exception when we try to
                    // get Content.Entities from it.

                    for fileName, signatures in assembliesByFileName do
                        let contentType =
                            if publicOnly then
                                AssemblyContentType.Public
                            else
                                AssemblyContentType.Full

                        let content =
                            AssemblyContent.GetAssemblyContent entityCache.Locking contentType fileName signatures

                        yield! content
                ]

            res
        with _ ->
            []

[<AbstractClass>]
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type AnalyzerAttribute(name: string, shortDescription: string, helpUri: string) =
    inherit Attribute()
    member val Name: string = name

    member val ShortDescription: string option =
        if String.IsNullOrWhiteSpace shortDescription then
            None
        else
            Some shortDescription

    member val HelpUri: string option =
        if String.IsNullOrWhiteSpace helpUri then
            None
        else
            Some helpUri

[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type CliAnalyzerAttribute
    (
        [<Optional; DefaultParameterValue "Analyzer">] name: string,
        [<Optional; DefaultParameterValue("" :> obj)>] shortDescription: string,
        [<Optional; DefaultParameterValue("" :> obj)>] helpUri: string
    )
    =
    inherit AnalyzerAttribute(name, shortDescription, helpUri)

    member _.Name = name

[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type EditorAnalyzerAttribute
    (
        [<Optional; DefaultParameterValue "Analyzer">] name: string,
        [<Optional; DefaultParameterValue("" :> obj)>] shortDescription: string,
        [<Optional; DefaultParameterValue("" :> obj)>] helpUri: string
    )
    =
    inherit AnalyzerAttribute(name, shortDescription, helpUri)

    member _.Name = name

type Context = interface end

type CliContext =
    {
        FileName: string
        SourceText: ISourceText
        ParseFileResults: FSharpParseFileResults
        CheckFileResults: FSharpCheckFileResults
        TypedTree: FSharpImplementationFileContents option
        CheckProjectResults: FSharpCheckProjectResults
        ProjectOptions: FSharpProjectOptions
    }

    interface Context

    member x.GetAllEntities(publicOnly: bool) =
        EntityCache.getEntities publicOnly x.CheckFileResults

    member x.GetAllSymbolUsesOfProject() =
        x.CheckProjectResults.GetAllUsesOfAllSymbols()

    member x.GetAllSymbolUsesOfFile() =
        x.CheckFileResults.GetAllUsesOfAllSymbolsInFile()

type EditorContext =
    {
        FileName: string
        SourceText: ISourceText
        ParseFileResults: FSharpParseFileResults
        CheckFileResults: FSharpCheckFileResults option
        TypedTree: FSharpImplementationFileContents option
        CheckProjectResults: FSharpCheckProjectResults option
        ProjectOptions: FSharpProjectOptions
    }

    interface Context

    member x.GetAllEntities(publicOnly: bool) : AssemblySymbol list =
        match x.CheckFileResults with
        | None -> List.empty
        | Some checkFileResults -> EntityCache.getEntities publicOnly checkFileResults

    member x.GetAllSymbolUsesOfProject() : FSharpSymbolUse array =
        match x.CheckProjectResults with
        | None -> Array.empty
        | Some checkProjectResults -> checkProjectResults.GetAllUsesOfAllSymbols()

    member x.GetAllSymbolUsesOfFile() : FSharpSymbolUse seq =
        match x.CheckFileResults with
        | None -> Seq.empty
        | Some checkFileResults -> checkFileResults.GetAllUsesOfAllSymbolsInFile()

type Fix =
    {
        FromRange: range
        FromText: string
        ToText: string
    }

[<RequireQualifiedAccess>]
type Severity =
    | Info
    | Hint
    | Warning
    | Error

type Message =
    {
        Type: string
        Message: string
        Code: string
        Severity: Severity
        Range: range
        Fixes: Fix list
    }

type Analyzer<'TContext> = 'TContext -> Async<Message list>

type AnalyzerMessage =
    {
        Message: Message
        Name: string
        AssemblyPath: string
        ShortDescription: string option
        HelpUri: string option
    }

type AnalysisFailure = | Aborted

module Utils =
    let currentFSharpAnalyzersSDKVersion =
        Assembly.GetExecutingAssembly().GetName().Version

    let currentFSharpCoreVersion =
        let currentAssembly = Assembly.GetExecutingAssembly()
        let references = currentAssembly.GetReferencedAssemblies()
        let fc = references |> Array.tryFind (fun ra -> ra.Name = "FSharp.Core")

        match fc with
        | None -> failwith "FSharp.Core could not be found as a reference assembly of the SDK."
        | Some fc -> fc.Version

    let createContext
        (checkProjectResults: FSharpCheckProjectResults)
        (fileName: string)
        (sourceText: ISourceText)
        ((parseFileResults: FSharpParseFileResults, checkFileResults: FSharpCheckFileResults))
        (projectOptions: FSharpProjectOptions)
        : CliContext
        =
        {
            FileName = fileName
            SourceText = sourceText
            ParseFileResults = parseFileResults
            CheckFileResults = checkFileResults
            TypedTree = checkFileResults.ImplementationFile
            CheckProjectResults = checkProjectResults
            ProjectOptions = projectOptions
        }

    let createFCS documentSource =
        let ds =
            documentSource
            |> Option.map DocumentSource.Custom
            |> Option.defaultValue DocumentSource.FileSystem

        FSharpChecker.Create(
            projectCacheSize = 200,
            keepAllBackgroundResolutions = true,
            keepAssemblyContents = true,
            documentSource = ds
        )

    [<RequireQualifiedAccess>]
    type SourceOfSource =
        | Path of string
        | DiscreteSource of string
        | SourceText of ISourceText

    let typeCheckFile
        (fcs: FSharpChecker)
        (logger: ILogger)
        (options: FSharpProjectOptions)
        (fileName: string)
        (source: SourceOfSource)
        : Result<FSharpParseFileResults * FSharpCheckFileResults, AnalysisFailure>
        =

        let sourceText =
            match source with
            | SourceOfSource.Path path ->
                let text = System.IO.File.ReadAllText path
                SourceText.ofString text
            | SourceOfSource.DiscreteSource s -> SourceText.ofString s
            | SourceOfSource.SourceText s -> s

        let parseRes, checkAnswer =
            fcs.ParseAndCheckFileInProject(fileName, 0, sourceText, options)
            |> Async.RunSynchronously //ToDo: Validate if 0 is ok

        match checkAnswer with
        | FSharpCheckFileAnswer.Aborted ->
            logger.LogError("Checking of file {0} aborted", fileName)
            Error AnalysisFailure.Aborted
        | FSharpCheckFileAnswer.Succeeded result -> Ok(parseRes, result)
