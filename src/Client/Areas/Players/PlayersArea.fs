namespace Areas.Players

open Elmish
open Shared
open Routes

module PlayersArea =

  type Model =
    | PlayerModel of Player.Model
    | AllPlayersModel of AllPlayers.Model
    | PlayerFixtureSetModel of PlayerFixtureSet.Model

  type Msg =
    | PlayerMsg of Player.Msg
    | AllPlayersMsg of AllPlayers.Msg
    | PlayerFixtureSetMsg of PlayerFixtureSet.Msg

  let update api p message model =
    match message, model with
    | PlayerMsg msg, PlayerModel m ->
      Player.update api p msg m |> fun (m, cmd) -> PlayerModel m, Cmd.map PlayerMsg cmd
    | AllPlayersMsg msg, AllPlayersModel m ->
      AllPlayers.update api p msg m |> fun (m, cmd) -> AllPlayersModel m, Cmd.map AllPlayersMsg cmd
    | PlayerFixtureSetMsg msg, PlayerFixtureSetModel m ->
      PlayerFixtureSet.update api p msg m |> fun (m, cmd) -> PlayerFixtureSetModel m, Cmd.map PlayerFixtureSetMsg cmd
    | _ -> model, alert (LoginProblem "player msg not matched")

  let urlUpdate api p = function
    | PlayerRoute playerId ->
      PlayerId playerId |> Player.init api p |> fun (m, cmd) -> PlayerModel m, Cmd.map PlayerMsg cmd
    | AllPlayersRoute ->
      AllPlayers.init api p |> fun (m, cmd) -> AllPlayersModel m, Cmd.map AllPlayersMsg cmd
    | PlayerFixtureSetRoute (playerId, fsId) when isValidGuid fsId ->
      toGuid fsId |> FixtureSetId |> PlayerFixtureSet.init api p (PlayerId playerId) |> fun (m, cmd) -> PlayerFixtureSetModel m, Cmd.map PlayerFixtureSetMsg cmd
    | _ ->
      AllPlayers.init api p |> fun (m, cmd) -> AllPlayersModel m, Cmd.map AllPlayersMsg cmd

  let view model dispatch =
    match model with
    | PlayerModel m ->
      Player.view m (PlayerMsg >> dispatch)
    | AllPlayersModel m ->
      AllPlayers.view m (AllPlayersMsg >> dispatch)
    | PlayerFixtureSetModel m ->
      PlayerFixtureSet.view m (PlayerFixtureSetMsg >> dispatch)
