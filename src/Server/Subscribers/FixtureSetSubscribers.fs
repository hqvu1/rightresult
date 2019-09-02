﻿namespace Server.Subscribers

open FSharp.Core
open System
open Shared
open Server
open Server.Infrastructure
open Persistence

module FixtureSubscribersAssistance =

  let leagueIdsAndNames deps =
    deps.Queries.getPrivateLeagues ()
    |> List.ofSeq
    |> List.map (fun l -> PrivateLeague l.PrivateLeagueId, l.LeagueName)
    |> fun l -> (GlobalLeague, Global.leagueName)::l

  let getPrivateLeaguesAndLeagueMembers deps =
    deps.Queries.getPrivateLeaguesAndMembers ()
    |> List.ofSeq
    |> List.map (fun (league, members) ->
      PrivateLeague league.PrivateLeagueId,
      league.LeagueName,
      members
      |> List.ofSeq
      |> List.map (fun m -> m.Id))

  let allLeaguesAndMembers deps =
    List.map (fun (p:PlayerRecord) -> p.Id)
    >> fun allPlayerIds -> (GlobalLeague, Global.leagueName, allPlayerIds) :: getPrivateLeaguesAndLeagueMembers deps

  let getPlayerPushSubscriptions (deps:Dependencies) : List<PlayerId * PushSubscription> =
    ElasticSearch.repo deps.ElasticSearch
    |> fun repo -> repo.Read PlayerPushSubscriptions
    |> Option.defaultValue []
    |> List.distinct


module FixtureSetCreatedSubscribers =

  open Infrastructure.PushNotifications

  let createFixtureSet (deps:Dependencies) created (FixtureSetId fsId, GameweekNo gwno, fixtures:FixtureRecord list) =
    fixtures
    |> List.minBy (fun { KickOff = KickOff ko } -> ko)
    |> fun { KickOff = KickOff minKo } ->
    { FixtureSetNode.Id = string fsId
      GameweekNo = gwno
      Year = minKo.DateTime.Year
      Month = minKo.DateTime.Month
      Created = created
      IsConcluded = false }
    |> deps.NonQueries.createFixtureSet

    fixtures
    |> List.sortBy (fun { KickOff = KickOff ko } -> ko)
    |> List.mapi (
      fun i { FixtureRecord.Id = FixtureId fId
              GameweekNo = GameweekNo gwno
              KickOff = KickOff ko
              TeamLine = TeamLine (Team home, Team away) } ->
      { FixtureNode.Id = string fId
        FixtureSetId = string fsId
        Created = created
        GameweekNo = gwno
        SortOrder = i
        KickOff = ko
        HomeTeam = home
        AwayTeam = away
        HasKickedOff = false
        HasResult = false
        HomeScore = 0
        AwayScore = 0 })
    |> List.iter (deps.NonQueries.createFixture (FixtureSetId fsId))

  let createMatrix (deps:Dependencies) created (fsId, gwno, fixtures:FixtureRecord list) =
    let columns =
      fixtures
      |> List.map (fun f ->
        f.Id,
        { MatrixFixture.TeamLine = f.TeamLine
          KickOff = f.KickOff
          State = MatrixFixtureState.Open
          SortOrder = f.SortOrder
        })
      |> Map.ofList
    ElasticSearch.repo deps.ElasticSearch
    |> fun repo ->
      FixtureSubscribersAssistance.leagueIdsAndNames deps
      |> List.iter (fun (leagueId, leagueName) ->
        { FixtureSetId = fsId
          LeagueName = leagueName
          LeagueId = leagueId
          GameweekNo = gwno
          Columns = columns
          Rows = Map.empty
        }
        |> repo.Insert (Matrix (leagueId, gwno))
      )

  let notifyPlayers (deps:Dependencies) created (fsId, GameweekNo gwno, _) =
    { PushMessage.Title = sprintf "GW %i fixtures added" gwno
      Body = "Get your predictions in!" }
    |> fun m ->
    FixtureSubscribersAssistance.getPlayerPushSubscriptions deps
    |> List.iter (fun (_, ps) -> deps.PushNotify m ps)

  let all =
    [ createFixtureSet
      createMatrix
      notifyPlayers
    ]

module FixtureSetConcludedSubscribers =

  let concludeFixtureSet (deps:Dependencies) _ (fsId, _) =
    deps.NonQueries.concludeFixtureSet fsId

  let calculateGlobalGameweekWinner (deps:Dependencies) created (fsId, GameweekNo gwno) =
    ElasticSearch.repo deps.ElasticSearch
    |> fun repo ->
    LeagueTableDocument (GlobalLeague, Week gwno)
    |> repo.Read
    |> Option.bind (fun table -> table.Members |> List.tryHead)
    |> Option.map (fun (playerId, m) ->
      ElasticSearch.repo deps.ElasticSearch
      |> fun repo ->
        repo.Insert
          GlobalGameweekWinner
          { GlobalGameweekWinner.PlayerId = playerId
            GameweekNo = GameweekNo gwno
            Member = m })
    |> ignore

  let all =
    [ concludeFixtureSet
      calculateGlobalGameweekWinner
    ]

module FixtureKoEditedSubscribers =

  let editFixtureKo (deps:Dependencies) _ (_, fId, ko) =
    deps.NonQueries.editFixtureKo (fId, ko)

  let all =
    [ editFixtureKo
    ]

module FixtureKickedOffSubscribers =

  let kickOffFixture (deps:Dependencies) _ (_, fId) =
    deps.NonQueries.kickOffFixture fId

  let updateMatrix (deps:Dependencies) _ (fsId, fId) =

    let q =
      deps.Queries

    let allPlayers =
      q.getAllPlayers ()

    let playerNameMap =
      allPlayers
      |> List.map (fun p -> p.Id, p.Name)
      |> Map.ofList

    ElasticSearch.repo deps.ElasticSearch
    |> fun repo ->
    q.getFixtureSetGameweekNo fsId
    |> fun gwno ->
    FixtureSubscribersAssistance.allLeaguesAndMembers deps allPlayers
    |> List.iter (fun (leagueId, _, members) ->
      (fun (m:MatrixDoc) ->
        { m with
            Columns =
              m.Columns.Add(fId, { m.Columns.[fId] with State = MatrixFixtureState.KickedOff })
            Rows =
              members
              |> List.map (fun pId ->
                q.getPlayerPredictionForFixture pId fId
                |> Option.map (fun p ->
                  { MatrixPrediction.Prediction = p.ScoreLine
                    IsDoubleDown = p.IsDoubleDown
                    Points = None })
                |> fun mPrediction ->
                m.Rows.TryFind pId
                |> fun mPlayer ->
                match mPlayer, mPrediction with
                | Some pl, Some pr ->
                  pId, { pl with Predictions = pl.Predictions.Add(fId, pr) }
                | Some pl, None ->
                  pId, pl
                | None, Some pr ->
                  pId, { MatrixPlayer.PlayerName = playerNameMap.[pId]; Predictions = [ fId, pr ] |> Map.ofList; TotalPoints = 0 }
                | _ ->
                  pId, { MatrixPlayer.PlayerName = playerNameMap.[pId]; Predictions = Map.empty; TotalPoints = 0 }
                )
                |> Map.ofList
          })
        |> repo.Edit (Matrix (leagueId, gwno))
        |> ignore
      )

  let updatePredictedPremTable deps _ (fsId, fId) =

    let repo =
      ElasticSearch.repo deps.ElasticSearch

    let q =
      deps.Queries

    let allPlayers =
      q.getAllPlayers ()

    let { FixtureRecord.TeamLine = TeamLine (homeTeam, awayTeam) } =
      q.getFixtureRecord fId

    allPlayers
    |> List.ofSeq
    |> List.choose (fun player -> q.getPlayerPredictionForFixture player.Id fId)
    |> List.iter (fun prediction ->
      Points.getHomeAndAwayPremTableRowDiff prediction.ScoreLine
      |> fun (homeRowDiff, awayRowDiff) ->
        repo.Upsert
          (PredictedPremTable prediction.PlayerId)
          PremTable.Init
          (fun table ->
            table.Rows
            |> Map.add homeTeam (table.Rows.[homeTeam] + homeRowDiff)
            |> Map.add awayTeam (table.Rows.[awayTeam] + awayRowDiff)
            |> fun rows -> { PremTable.Rows = rows } ))

  let all =
    [ kickOffFixture
      updateMatrix
      updatePredictedPremTable
    ]

module FixtureClassifiedSubscribers =

  let fixturePredictionToPoints (f:FixtureRecord, p:PredictionRecord) =
    match f.ScoreLine with
    | Some result ->
      Some (p.ScoreLine, p.IsDoubleDown)
      |> Points.getPointsForPrediction result
      |> fst
    | _ ->
      PredictionPointsMonoid.Init

  type PositionNumber = PositionNumber of int
  type PositionCollection = PositionCollection of (PlayerId * LeagueTableMember) list

  let sort (ppm:PredictionPointsMonoid) =
    ppm.Points,
    ppm.CorrectScores + ppm.DoubleDownCorrectScores,
    ppm.CorrectResults + ppm.DoubleDownCorrectResults

  let standingAlgo =
    List.sortByDescending (fun (_, m:LeagueTableMember) -> sort m.Points)
    >> List.groupBy (fun (_, m) -> sort m.Points)
    >> List.map (fun (_, col) -> col.Length, PositionCollection col)
    >> List.fold (fun (totalCount, accPlayers) (_, PositionCollection players) ->
      totalCount+players.Length, (PositionNumber totalCount, PositionCollection players)::accPlayers)
      (1, [])
    >> fun (_, pc) -> pc |> List.map (fun (PositionNumber n, PositionCollection players) -> players |> List.map (fun (pId, mbr) -> pId, { mbr with Position = n }))
    >> List.collect id
    >> List.rev

  let updateAllLeagueTables (deps:Dependencies) _ (FixtureSetId fsId, _, _) =

    let q =
      deps.Queries

    let (GameweekNo gwno) =
      q.getFixtureSetGameweekNo (FixtureSetId fsId)

    let (year, month) =
      q.getFixtureSetYearAndMonth (FixtureSetId fsId)

    let allPlayers =
      q.getAllPlayers ()

    let playerNameMap =
      allPlayers
      |> List.map (fun p -> p.Id, p.Name)
      |> Map.ofList

    let buildLeagueTable (leagueName, document, (playerPredictions:Map<PlayerId, (FixtureRecord * PredictionRecord) list>)) =

      ElasticSearch.repo deps.ElasticSearch
      |> fun repo ->
      playerPredictions
      |> Map.map (fun playerId fixturePredictions ->
        fixturePredictions
        |> List.map fixturePredictionToPoints
        |> List.fold (+) PredictionPointsMonoid.Init
        |> fun m ->
          playerNameMap.TryFind playerId
          |> Option.map (fun playerName ->
             { Position = 0
               Movement = 0
               PlayerName = playerName
               Points = m }))
      |> Map.toList
      |> List.filter (snd >> Option.isSome)
      |> List.map (fun (p, o) -> p, o.Value)
      |> standingAlgo
      |> fun members ->
        { LeagueTableDoc.LeagueName = leagueName
          Members = members }
      |> repo.Insert document


    let membersToPredictionMap f =
      List.map (fun playerId -> playerId, f playerId) >> Map.ofList

    FixtureSubscribersAssistance.allLeaguesAndMembers deps allPlayers
    |> List.iter (fun (leagueId, leagueName, members) ->
      [ leagueName, LeagueTableDocument (leagueId, Full), members |> membersToPredictionMap q.getPredictionsForPlayer
        leagueName, LeagueTableDocument (leagueId, Week gwno), members |> membersToPredictionMap (q.getPredictionsForPlayerInFixtureSet (FixtureSetId fsId))
        leagueName, LeagueTableDocument (leagueId, Month (year, month)), members |> membersToPredictionMap (q.getPredictionsForPlayerInMonth (year, month))
      ]
      |> List.iter buildLeagueTable)

  let updateFixtureGraph (deps:Dependencies) _ (_, fId, scoreLine) =
    deps.NonQueries.classifyFixture (fId, scoreLine)

  let updatePlayerFixtureSetsDoc (deps:Dependencies) _ (fsId, _, _) =
    let q =
      deps.Queries
    let gwno =
      q.getFixtureSetGameweekNo fsId
    q.getAllPlayers ()
    |> List.iter (fun player ->
        q.getPredictionsForPlayerInFixtureSet fsId player.Id
        |> List.map fixturePredictionToPoints
        |> List.fold (+) PredictionPointsMonoid.Init
        |> fun m ->
          { GameweekNo = gwno
            AveragePoints = 0.
            PlayerPoints = m }
        |> fun docRow ->
          ElasticSearch.repo deps.ElasticSearch
          |> fun repo ->
            repo.Upsert
              (PlayerFixtureSetsDocument player.Id)
              (PlayerFixtureSetsDoc.Init player.Id)
              (fun pfsd -> { pfsd with FixtureSets = pfsd.FixtureSets.Add (fsId, docRow) }))

  let updateLeagueHistoryWindowDoc (deps:Dependencies) (docF, window, description) =

    let getLeagueTable (leagueId, window) : LeagueTableDoc option =
      ElasticSearch.repo deps.ElasticSearch
      |> fun repo -> repo.Read (LeagueTableDocument (leagueId, window))

    let leagueIds =
      deps.Queries.getPrivateLeagues ()
      |> List.ofSeq
      |> List.map (fun l -> PrivateLeague l.PrivateLeagueId)
      |> fun l -> GlobalLeague::l

    leagueIds
    |> List.iter (fun leagueId ->
      match getLeagueTable (leagueId, window) with
      | Some table ->
        table.Members
        |> List.tryHead // TODO: everyone in position 1
        |> function
        | Some (_, m) ->
          ElasticSearch.repo deps.ElasticSearch
          |> (fun repo ->
            repo.Upsert (docF leagueId)
              Map.empty
              (fun d ->
                d.Add(window,
                  { LeagueHistoryUnitWinner.PlayerName = m.PlayerName
                    Description = description
                    Points = m.Points })))
        | None -> ()
      | None -> ())

  let updateLeagueHistoryFixtureSetDoc deps _ (FixtureSetId fsId, _, _) =
    deps.Queries.getFixtureSetAndEarliestKo (FixtureSetId fsId)
    |> fun ({ FixtureRecord.GameweekNo = GameweekNo gwno }, _) ->
    (LeagueAllFixtureSetHistory, Week gwno, sprintf "Gameweek %i" gwno)
    |> updateLeagueHistoryWindowDoc deps

  let updateLeagueHistoryMonthSetDoc deps _ (FixtureSetId fsId, _, _) =
    deps.Queries.getFixtureSetAndEarliestKo (FixtureSetId fsId)
    |> fun (_, earliestKo) ->
    (LeagueAllMonthHistory, Month (earliestKo.DateTime.Year, earliestKo.Month), (earliestKo.ToString("MMMM yyyy")))
    |> updateLeagueHistoryWindowDoc deps

  let updateMatrixDoc deps _ (fsId, fId, resultScoreLine) =

    let q =
      deps.Queries

    let allPlayers =
      q.getAllPlayers ()

    let playerNameMap =
      allPlayers
      |> List.map (fun p -> p.Id, p.Name)
      |> Map.ofList

    ElasticSearch.repo deps.ElasticSearch
    |> fun repo ->
    q.getFixtureSetGameweekNo fsId
    |> fun gwno ->
    FixtureSubscribersAssistance.allLeaguesAndMembers deps allPlayers
    |> List.iter (fun (leagueId, _, members) ->
      (fun (m:MatrixDoc) ->
        { m with
            Columns =
              m.Columns.Add(fId, { m.Columns.[fId] with State = MatrixFixtureState.Classified resultScoreLine })
            Rows =
              members
              |> List.map (fun pId ->
                match m.Rows.TryFind pId with
                | Some mPlayer ->
                  match mPlayer.Predictions.TryFind fId with
                  | Some mPrediction ->
                    Points.getPointsForPrediction resultScoreLine (Some (mPrediction.Prediction, mPrediction.IsDoubleDown))
                    |> fun (m, cat) ->
                      mPlayer.Predictions.Add(fId, { mPrediction with Points = Some (m.Points, cat) })
                      |> fun predictions ->
                        predictions
                        |> Map.toList
                        |> List.sumBy (fun (_, p) ->
                          match p.Points with
                          | Some (points, _) -> points
                          | None -> 0)
                        |> fun totalPoints ->
                          pId, { mPlayer with Predictions = predictions; TotalPoints = totalPoints }
                  | None ->
                    pId, mPlayer
                | None ->
                  pId, { MatrixPlayer.PlayerName = playerNameMap.[pId]; Predictions = Map.empty; TotalPoints = 0 })
              |> Map.ofList
          })
        |> repo.Edit (Matrix (leagueId, gwno))
        |> ignore)

  let updateRealPremTable deps _ (_, fId, resultScoreLine) =

    let repo =
      ElasticSearch.repo deps.ElasticSearch

    let { FixtureRecord.TeamLine = TeamLine (homeTeam, awayTeam) } =
      deps.Queries.getFixtureRecord fId

    let (homeRowDiff, awayRowDiff) =
      Points.getHomeAndAwayPremTableRowDiff resultScoreLine

    repo.Upsert
      RealPremTable
      PremTable.Init
      (fun table ->
        table.Rows
        |> Map.add homeTeam (table.Rows.[homeTeam] + homeRowDiff)
        |> Map.add awayTeam (table.Rows.[awayTeam] + awayRowDiff)
        |> fun r -> { PremTable.Rows = r })

  let all =
    [ updateFixtureGraph
      updateAllLeagueTables
      updatePlayerFixtureSetsDoc
      updateLeagueHistoryFixtureSetDoc
      updateLeagueHistoryMonthSetDoc
      updateMatrixDoc
      updateRealPremTable
    ]
