namespace Server.HttpHandlers

open System
open FSharp.Data
open Server.Commands
open Server.Queries
open Shared
open Server.Infrastructure
open Server.Infrastructure.Persistence
open Server.Infrastructure.Time
open Server.Infrastructure.PushNotifications

module FixtureSourcing =

  type PremFixtures =
    JsonProvider<Sample="HttpHandlers/PremFixturesSample.json">

  let private premFixturesUrl =
    sprintf "https://fantasy.premierleague.com/api/fixtures/?event=%i"

  open Shared.Teams

  let private premTeamIdToName = function
    | 1 -> Arsenal
    | 2 -> AstonVilla
    | 3 -> Brighton
    | 4 -> Burnley
    | 5 -> Chelsea
    | 6 -> CrystalPalace
    | 7 -> Everton
    | 8 -> Fulham
    | 9 -> Leicester
    | 10 -> Leeds
    | 11 -> Liverpool
    | 12 -> ManCity
    | 13 -> ManUtd
    | 14 -> Newcastle
    | 15 -> SheffieldUtd
    | 16 -> Southampton
    | 17 -> Spurs
    | 18 -> WestBrom
    | 19 -> WestHam
    | 20 -> Wolves
    | _ -> failwith "Unrecognised team id"

  let private toTeam =
    premTeamIdToName >> Team

  let private getNewPremGwFixtures no =
    PremFixtures.Load(premFixturesUrl no)
    |> Seq.map(fun f -> toUkTime f.KickoffTime.DateTime, f.TeamH |> premTeamIdToName, f.TeamA |> premTeamIdToName)
    |> Seq.toList

  let getNewPremGwResults no =
    PremFixtures.Load(premFixturesUrl no)
    |> Seq.filter (fun f -> f.Started && f.FinishedProvisional)
    |> Seq.map (fun f ->
        TeamLine (toTeam f.TeamH, toTeam f.TeamA),
        ScoreLine (Score (f.TeamHScore.JsonValue.AsInteger()), Score (f.TeamAScore.JsonValue.AsInteger())))
    |> Seq.toList

  let private getNewGameweekNo (deps:Dependencies) =
    deps.Queries.getMaxGameweekNo ()
    |> function
    | Some (GameweekNo gw) -> gw
    | _ -> 0
    |> (+) 1

  let getNewFixtureSetViewModel (deps:Dependencies) =
    deps
    |> (getNewGameweekNo
      >> (fun gwno ->
      getNewPremGwFixtures gwno
      |> List.map (fun (ko, h, a) -> KickOff ko, KickOff.groupFormat (KickOff ko), Team h, Team a)
      |> fun items -> { NewFixtureSetViewModel.GameweekNo = GameweekNo gwno; Fixtures = items }))

  let addNewFixtureSet (deps:Dependencies) =
    deps
    |> (getNewGameweekNo
      >> (fun gwno ->
      FixtureSetId (Guid.NewGuid())
      |> fun fsId ->
      getNewPremGwFixtures gwno
      |> List.sortBy (fun (ko, _, _) -> ko)
      |> List.mapi (fun i (ko, h, a) ->
        { FixtureRecord.Id = FixtureId (Guid.NewGuid())
          FixtureSetId = fsId
          GameweekNo = GameweekNo gwno
          KickOff = KickOff ko
          TeamLine = TeamLine (Team h, Team a)
          ScoreLine = None
          SortOrder = i })
      |> fun fixtures -> GameweekNo gwno, fixtures
      |> CreateFixtureSet
      |> fun fscmd -> FixtureSetCommand (fsId, fscmd)))

module Classifier =

  let private classifyFixtures desc (handle:Command -> Ars<Unit>) (q:Queries) (fixturesFunc:Queries -> FixtureRecord seq) =
    let fixtures =
      fixturesFunc q
      |> List.ofSeq
    fixtures
    |> List.map (fun f -> f.GameweekNo)
    |> List.distinct
    |> List.map (fun (GameweekNo gwno) -> FixtureSourcing.getNewPremGwResults gwno)
    |> List.collect id
    |> List.iter (fun (teamLine, scoreLine) ->
      fixtures
      |> List.tryFind (fun f -> f.TeamLine = teamLine)
      |> function
      | Some f ->
        ClassifyFixture (f.Id, scoreLine)
        |> fun cmd -> FixtureSetCommand (f.FixtureSetId, cmd)
        |> handle
        |> Async.RunSynchronously
        |> function
        | Ok _ -> printfn "%s Fixture Classified\n%A\n%A" desc f scoreLine
        | Error e -> printfn "ERROR CLASSIFYING\n%A" e
      | None -> ())

  let classifyKickedOffFixtures (handle:Command -> Ars<Unit>) (q:Queries) =
    classifyFixtures "@@@@@@@@@@@@@@@@@ KICKED OFF FIXTURES" handle q (fun q -> q.getFixturesAwaitingResults ())

  // let classifyAllFixtures (handle:Command -> Ars<Unit>) (q:Queries) =
  //   classifyFixtures "@@@@@@@@@@@@@@@@@ CLASS ALL" handle q (fun q -> q.getAllFixtures ())

  let classifyFixturesAfterGameweek (handle:Command -> Ars<Unit>) (q:Queries) (GameweekNo gwno) =
    classifyFixtures "@@@@@@@@@@@@@@@@@ CLASS AFTER GW" handle q (fun q -> q.getAllFixtures () |> Seq.filter(fun { GameweekNo = GameweekNo g } -> g > gwno))

  let concludeGameweek (handle:Command -> Ars<Unit>) (q:Queries) =
    q.getUnconcludedFixtureSets ()
    |> Seq.iter (fun (fsId, gwno, fixtures) ->
      if fixtures |> Seq.forall (fun f -> f.ScoreLine.IsSome) then
        printfn "CONCLUDING GW %A %A" fsId gwno
        FixtureSetCommand (fsId, ConcludeFixtureSet gwno)
        |> handle
        |> Async.RunSynchronously
        |> ignore
      else ())

module Whistler =

  let kickOffFixtures (handle:Command -> Ars<Unit>) (q:Queries) (now:DateTimeOffset) =
    q.getKickedOffFixtures now
    |> List.ofSeq
    |> List.iter (
      fun f ->
        KickOffFixture f.Id
        |> fun cmd -> FixtureSetCommand (f.FixtureSetId, cmd)
        |> handle
        |> Async.RunSynchronously
        |> function
        | Ok _ -> printfn "Fixture kicked off\n%A\n%A" f.Id f.TeamLine
        | Error e -> printfn "ERROR KICKING OFF\n%A" e)

module HttpHandlers =

  open Microsoft.AspNetCore.Http
  open Giraffe
  open Server.Elevated

  [<CLIMutable>]
  type FixtureSetHttp =
    { GameweekNo : int
      Fixtures : FixtureHttp list }
  and FixtureHttp =
    { FixtureId : Guid
      Home : string
      Away : string
      KickOff : DateTimeOffset }

  let createFixtureSet handleCommand next (ctx:HttpContext) =
    let respond next ctx (result:Rresult<Unit>) =
      match result with
      | Ok () -> Successful.CREATED "Created" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    let getFixtureSetFromRequest (ctx:HttpContext) =
      ctx.BindModelAsync<FixtureSetHttp>()
    let createFixtureSetCmd (fs:FixtureSetHttp) =
      fs.Fixtures
      |> List.map (fun f ->
          FixtureSetId (Guid.NewGuid())
          |> fun fsId ->
          { FixtureRecord.Id = FixtureId (Guid.NewGuid())
            FixtureSetId = fsId
            GameweekNo = GameweekNo fs.GameweekNo
            KickOff = KickOff f.KickOff
            TeamLine = TeamLine (Team f.Home, Team f.Away)
            ScoreLine = None
            SortOrder = 0 })
      |> fun fixtures -> GameweekNo fs.GameweekNo, fixtures
      |> CreateFixtureSet
      |> fun fscmd -> FixtureSetCommand (FixtureSetId (Guid.NewGuid()), fscmd)
    ctx
    |> (getFixtureSetFromRequest
    >> Task.toAsync
    >> Async.map createFixtureSetCmd
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))

  [<CLIMutable>]
  type EditFixtureKoHttp =
    { FixtureSetId : Guid
      FixtureId : Guid
      KickOff : DateTimeOffset }

  let editFixtureKo handleCommand next (ctx:HttpContext) =
    let respond next ctx (result:Rresult<Unit>) =
      match result with
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    let editFixtureKoCmd (e:EditFixtureKoHttp) =
      (FixtureId e.FixtureId, KickOff e.KickOff)
      |> EditFixtureKickOff
      |> fun fscmd -> FixtureSetCommand (FixtureSetId e.FixtureSetId, fscmd)
    ctx
    |> (fun ctx -> ctx.BindModelAsync<EditFixtureKoHttp>()
    >> Task.toAsync
    >> Async.map editFixtureKoCmd
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))

  [<CLIMutable>]
  type AppendFixtureHttp =
    { FixtureSetId : Guid
      Home : string
      Away : string
      KickOff : DateTimeOffset }

  let appendFixtureToGameweek handleCommand next (ctx:HttpContext) =
    let respond next ctx = function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    ctx
    |> (fun ctx -> ctx.BindModelAsync<AppendFixtureHttp>()
    >> Task.toAsync
    >> Async.map (fun e ->
      { Id = FixtureId (Guid.NewGuid())
        FixtureSetId = FixtureSetId e.FixtureSetId
        GameweekNo = GameweekNo 0
        KickOff = KickOff e.KickOff
        TeamLine = TeamLine (Team e.Home, Team e.Away)
        ScoreLine = None
        SortOrder = 0 }
      |> AppendFixture
      |> fun fscmd -> FixtureSetCommand (FixtureSetId e.FixtureSetId, fscmd))
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))

  [<CLIMutable>]
  type RemoveOpenFixtureHttp =
    { FixtureId : Guid
      FixtureSetId : Guid }

  let removeOpenFixture handleCommand next (ctx:HttpContext) =
    let respond next ctx = function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    ctx
    |> (fun ctx -> ctx.BindModelAsync<RemoveOpenFixtureHttp>()
    >> Task.toAsync
    >> Async.map (fun e ->
      FixtureId e.FixtureId
      |> RemoveOpenFixture
      |> fun fscmd -> FixtureSetCommand (FixtureSetId e.FixtureSetId, fscmd))
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))

  [<CLIMutable>]
  type FixtureClassificationHttp =
    { HomeTeam : string
      AwayTeam : string
      HomeScore : int
      AwayScore : int }

  let classifyFixture (deps:Dependencies) handleCommand next (ctx:HttpContext) =
    let respond next ctx (result:Rresult<Unit>) =
      match result with
      | Ok () -> Successful.CREATED "Created" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    let getClassificationFromRequest (ctx:HttpContext) =
      ctx.BindModelAsync<FixtureClassificationHttp>()
    let getFixtureFromRequest (fc:FixtureClassificationHttp) =
      deps.Queries.getFixtureByTeams (Team fc.HomeTeam) (Team fc.AwayTeam)
    let createClassifyFixtureCmd (fc:FixtureClassificationHttp, fr:FixtureRecord) =
      ClassifyFixture (fr.Id, ScoreLine (Score fc.HomeScore, Score fc.AwayScore))
      |> fun fscmd -> FixtureSetCommand (fr.FixtureSetId, fscmd)
    ctx
    |> (getClassificationFromRequest
    >> Task.toAsync
    >> Async.map (fun fc -> (fc, getFixtureFromRequest fc) |> createClassifyFixtureCmd)
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))

  // let classifyAllFixtures (deps:Dependencies) handleCommand =
  //   Classifier.classifyAllFixtures handleCommand deps.Queries
  //   Successful.OK "Ok"

  let classifyFixturesAfterGameweek (deps:Dependencies) handleCommand gwno =
    Classifier.classifyFixturesAfterGameweek handleCommand deps.Queries (GameweekNo gwno)
    Successful.OK "Ok"

  [<CLIMutable>]
  type RemovePlayerHttp =
    { PlayerId : string }

  let removePlayer handleCommand =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<RemovePlayerHttp>()
      |> Task.map (fun p -> (PlayerId p.PlayerId, Remove) |> PlayerCommand)
      |> Task.bind (Async.toTask handleCommand)
      |> Task.bind (function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx)

  [<CLIMutable>]
  type AddPlayerToLeagueHttp =
    { PlayerId : string
      LeagueId : Guid }

  let addPlayerToLeague handleCommand =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<AddPlayerToLeagueHttp>()
      |> Task.map (fun p -> (PrivateLeagueId p.LeagueId, PlayerId p.PlayerId |> JoinLeague) |> PrivateLeagueCommand)
      |> Task.bind (Async.toTask handleCommand)
      |> Task.bind (function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx)

  [<CLIMutable>]
  type RemovePlayerFromLeagueHttp =
    { PlayerId : string
      LeagueId : Guid }

  let removePlayerFromLeague handleCommand =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<RemovePlayerFromLeagueHttp>()
      |> Task.map (fun p -> (PrivateLeagueId p.LeagueId, PlayerId p.PlayerId |> LeaveLeague) |> PrivateLeagueCommand)
      |> Task.bind (Async.toTask handleCommand)
      |> Task.bind (function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx)


  [<CLIMutable>]
  type RenameLeagueHttp =
    { LeagueName : string
      LeagueId : Guid }

  let renameLeague handleCommand =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<RenameLeagueHttp>()
      |> Task.map (fun r -> PrivateLeagueCommand(PrivateLeagueId r.LeagueId, RenameLeague (LeagueName r.LeagueName)))
      |> Task.bind (Async.toTask handleCommand)
      |> Task.bind (function
        | Ok () -> Successful.OK "Ok" next ctx
        | Error s -> ServerErrors.INTERNAL_ERROR s next ctx)


  [<CLIMutable>]
  type OverwritePredictionSetHttp =
    { SourcePlayerId : string
      DestinationPlayerId : string
      FixtureSetId : Guid }

  let overwritePredictionSet handleCommand =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<OverwritePredictionSetHttp>()
      |> Task.map (fun m ->
        PlayerId m.SourcePlayerId
        |> OverwritePredictionSet
        |> fun cmd -> PredictionSetCommand (PlayerId m.DestinationPlayerId, FixtureSetId m.FixtureSetId, cmd))
      |> Task.bind (Async.toTask handleCommand)
      |> Task.bind (function
      | Ok () -> Successful.OK "Ok" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx)

  [<CLIMutable>]
  type KickOffFixtureHttp =
    { HomeTeam : string
      AwayTeam : string
    }

  let kickOffFixture (deps:Dependencies) handleCommand next (ctx:HttpContext) =
    let respond next ctx (result:Rresult<Unit>) =
      match result with
      | Ok () -> Successful.CREATED "Created" next ctx
      | Error s -> ServerErrors.INTERNAL_ERROR s next ctx
    let getKoFromRequest (ctx:HttpContext) =
      ctx.BindModelAsync<KickOffFixtureHttp>()
    let getFixtureFromRequest (ko:KickOffFixtureHttp) =
      deps.Queries.getFixtureByTeams (Team ko.HomeTeam) (Team ko.AwayTeam)
    let createKoFixtureCmd (fr:FixtureRecord) =
      KickOffFixture fr.Id
      |> fun fscmd -> FixtureSetCommand (fr.FixtureSetId, fscmd)
    ctx
    |> (getKoFromRequest
    >> Task.toAsync
    >> Async.map (getFixtureFromRequest >> createKoFixtureCmd)
    >> Async.toTask (Async.bind handleCommand)
    >> Task.bind (respond next ctx))


  [<CLIMutable>]
  type TestNotifyHttp =
    { Title : string
      Body : string
      PlayerId : string }

  let getPlayerPushSubscriptions (deps:Dependencies) : List<PlayerId * PushSubscription> =
    ElasticSearch.repo deps.ElasticSearch
    |> fun repo -> repo.Read PlayerPushSubscriptions
    |> Option.defaultValue []
    |> List.distinct

  let testNotify (deps:Dependencies) =
    fun next (ctx:HttpContext) ->
      ctx.BindModelAsync<TestNotifyHttp>()
      |> Task.map (
          fun t ->
            getPlayerPushSubscriptions deps
            |> List.filter (fun (pId, _) -> pId = PlayerId t.PlayerId)
            |> List.iter (fun (_, ps) -> deps.PushNotify { PushMessage.Title = t.Title; Body = t.Body } ps)
            Successful.OK "Ok" next ctx)
      |> Task.toAsync
      |> Async.RunSynchronously

  let backgroundTasks handleCommand queries now =
    Whistler.kickOffFixtures handleCommand queries (now())
    Classifier.concludeGameweek handleCommand queries
    Classifier.classifyKickedOffFixtures handleCommand queries

  let runBackgroundTasks (deps:Dependencies) handleCommand now =
    fun next (ctx:HttpContext) ->
      backgroundTasks handleCommand deps.Queries now
      Successful.OK "Ok" next ctx