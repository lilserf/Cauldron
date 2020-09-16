﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Cauldron
{

	public class GameCompleteEventArgs
	{
		public GameCompleteEventArgs(IEnumerable<GameEvent> gameEvents)
		{
			GameEvents = gameEvents;
		}

		public IEnumerable<GameEvent> GameEvents;
	}

	/// <summary>
	/// Basic parser that takes Game json updates and emits GameEvent json objects
	/// PLEASE NOTE this is probably wrong; I'm currently emitting one GameEvent per game update but
	/// ultimately we want a Retrosheet-style condensed description of the whole "play" as a single GameEvent
	/// </summary>
	public class GameEventParser
	{
		// Last state we saw, for comparison
		Game m_oldState;

		// State tracking for stats not tracked inherently in the state updates
		int m_eventIndex = 0;
		int m_batterCount = 0;
		// Map of pitcher IDs indexed by batter ID; used in attributing baserunners to pitchers
		Dictionary<string, string> m_responsiblePitchers;

		// Reconstruct the team linup from game data, used in identifying unknown batters
		string[] m_homePlayerLineup;
		string[] m_awayPlayerLineup;

		// Keep of track of whether we've had a valid batter for this inning
		HashSet<string> m_startedInnings;

		// Properties for metrics
		public int Discards => m_discards;
		int m_discards = 0;
		public int Processed => m_processed;
		int m_processed = 0;
		public int Errors => m_errors;
		int m_errors = 0;
		public int Fixes => m_fixes;
		int m_fixes = 0;
		public string GameId => m_gameId;
		string m_gameId;

		// Event currently being appended to
		GameEvent m_currEvent;

		// Map of player IDs indexed by name; used in looking up players who were incinerated or ate a peanut
		//Dictionary<string, string> m_playerNameToId;

		HttpClient m_client;


		public event EventHandler<GameCompleteEventArgs> GameComplete;
		public bool IsGameComplete
		{
			get; set;
		}
		private bool m_sentGameComplete;

		public IEnumerable<GameEvent> GameEvents => m_gameEvents;
		private List<GameEvent> m_gameEvents;

		public void StartNewGame(Game initState, DateTime timeStamp)
		{
			m_client = new HttpClient();
			m_client.BaseAddress = new Uri("https://api.blaseball-reference.com/v1/");
			m_client.DefaultRequestHeaders.Accept.Clear();
			m_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			//m_playerNameToId = new Dictionary<string, string>();
			m_homePlayerLineup = new string[10];
			m_awayPlayerLineup = new string[10];
			m_oldState = initState;
			m_eventIndex = 0;
			m_batterCount = 0;
			m_discards = 0;
			m_processed = 0;
			m_errors = 0;
			m_fixes = 0;
			m_gameId = initState.gameId;
			m_responsiblePitchers = new Dictionary<string, string>();
			m_startedInnings = new HashSet<string>();

			m_currEvent = CreateNewGameEvent(initState, timeStamp);
			m_currEvent.eventText.Add(initState.lastUpdate);

			m_gameEvents = new List<GameEvent>();
			IsGameComplete = false;
			m_sentGameComplete = false;
		}

		#region Inning tracking
		private string MakeInningKey(Game newState)
		{
			return newState.topOfInning ? $"T{newState.inning}" : $"B{newState.inning}";
		}

		private bool CanStartInning(Game newState)
		{
			return !m_startedInnings.Contains(MakeInningKey(newState)) && newState.BatterId != null;
		}

		private void StartInning(Game newState)
		{
			m_startedInnings.Add(MakeInningKey(newState));
		}
		#endregion

		private void AddParsingError(GameEvent e, string message) 
		{
			if (e != null)
			{
				e.parsingError = true;
				e.parsingErrorList.Add(message);
			}
			m_errors++;
		}

		private void AddFixedError(GameEvent e, string message)
		{
			if (e != null)
			{
				e.fixedError = true;
				e.fixedErrorList.Add(message);
			}
			m_fixes++;
		}
	
		private bool IsNextHalfInning(Game oldState, Game newState)
		{
			// Assumes no gaps
			return ((newState.topOfInning != oldState.topOfInning) &&
					(newState.inning - oldState.inning <= 1));
		}

		private bool IsStartOfInningMessage(Game newState) {
			return (newState.lastUpdate.Contains("Top of ") || newState.lastUpdate.Contains("Bottom of "));
		}

		private bool IsGameNearlyOver(Game oldState, Game newState)
		{
			return ((newState.inning >= 8) &&
					(oldState.halfInningOuts == 2 && newState.halfInningOuts == 0) &&
					((!newState.topOfInning) || (newState.topOfInning && newState.homeScore > newState.awayScore)));
		}

		private bool IsStartOfNextAtBat(Game oldState, Game newState, int tolerance = 0)
		{
			// Assumes no gaps
			int totalBattingGap = (newState.homeTeamBatterCount - oldState.homeTeamBatterCount) +
								  (newState.awayTeamBatterCount - oldState.awayTeamBatterCount);

			bool sameTeam = ((totalBattingGap == 1) &&
							 (newState.atBatStrikes + newState.atBatBalls <= tolerance));
		
			bool diffTeam = ((IsNextHalfInning(oldState, newState) || IsGameNearlyOver(oldState, newState)) &&
							 ((totalBattingGap == 0) || (totalBattingGap == -1 && newState.lastUpdate.Contains("caught"))) &&
							 (newState.atBatStrikes + newState.atBatBalls <= tolerance));

			bool startOfGame = ((newState.inning == 0 && newState.topOfInning == true )&&
								(oldState.homeTeamBatterCount == 0) &&
								(oldState.awayTeamBatterCount == 0) &&
								(newState.homeTeamBatterCount == -1) &&
								(newState.awayTeamBatterCount == -1));

			return sameTeam || diffTeam || startOfGame;
		}

		private bool IsEndOfCurrentAtBat(Game oldState, Game newState) {
			// Assumes no gaps
			return ((newState.atBatBalls <= oldState.atBatBalls) && 
					(newState.atBatStrikes <= oldState.atBatStrikes) &&
					(newState.atBatStrikes == 0 && newState.atBatBalls == 0));
		}

		private bool IsSameAtBat(Game oldState, Game newState)
		{
			return ((oldState.inning == newState.inning) &&
					(oldState.topOfInning == newState.topOfInning) &&
					(oldState.homeTeamBatterCount == newState.homeTeamBatterCount) &&
					(oldState.awayTeamBatterCount == newState.awayTeamBatterCount));
		}


		// If the Id and name are valid, store them in our map
		private void CapturePlayerId(string id, string name)
		{
			if(id != null && name != null && id != "" && name != "")
			{
				//m_playerNameToId[name] = id;
			}
		}

		// Capture available player IDs and names from the state
		private void CapturePlayerIds(Game state)
		{
			CapturePlayerId(state.awayBatter, state.awayBatterName);
			CapturePlayerId(state.awayPitcher, state.awayPitcherName);
			CapturePlayerId(state.homeBatter, state.homeBatterName);
			CapturePlayerId(state.homePitcher, state.homePitcherName);
		}

		private GameEvent CreateNewGameEvent(Game newState, DateTime timeStamp)
		{
			CapturePlayerIds(newState);

			GameEvent currEvent = new GameEvent();
			currEvent.parsingError = false;
			currEvent.parsingErrorList = new List<string>();
			currEvent.fixedError = false;
			currEvent.fixedErrorList = new List<string>();

			currEvent.firstPerceivedAt = timeStamp;

			currEvent.gameId = newState.gameId;
			currEvent.season = newState.season;
			currEvent.day = newState.day;
			currEvent.eventIndex = m_eventIndex;
			currEvent.batterCount = m_batterCount;
			currEvent.inning = newState.inning;
			currEvent.topOfInning = newState.topOfInning;
			currEvent.outsBeforePlay = m_oldState.halfInningOuts;

			currEvent.homeStrikeCount = newState.homeStrikes.GetValueOrDefault();
			currEvent.awayStrikeCount = newState.awayStrikes.GetValueOrDefault();

			currEvent.homeScore = newState.homeScore;
			currEvent.awayScore = newState.awayScore;

			// Currently not supported by the cultural event of Blaseball
			currEvent.isPinchHit = false;
			currEvent.isWildPitch = false;
			currEvent.isBunt = false;
			currEvent.errorsOnPlay = 0;

			currEvent.batterId = newState.BatterId;
			currEvent.batterTeamId = newState.BatterTeamId;
			currEvent.pitcherId = newState.PitcherId;
			currEvent.pitcherTeamId = newState.PitcherTeamId;

			currEvent.eventText = new List<string>();
			currEvent.pitchesList = new List<char>();
			currEvent.outcomes = new List<Outcome>();

			// Might be incorrect
			currEvent.totalStrikes = newState.atBatStrikes;
			currEvent.totalBalls = newState.atBatBalls;

			return currEvent;
		}

		/// <summary>
		/// Logic for updating balls and strikes, including foul balls
		/// </summary>
		private void UpdateBallsAndStrikes(Game newState)
		{
			int newStrikes = 0;
			int newBalls = 0;
			if(IsSameAtBat(m_oldState, newState) && !IsEndOfCurrentAtBat(m_oldState, newState))
			{
				newStrikes = newState.atBatStrikes - m_currEvent.totalStrikes;
				newBalls = newState.atBatBalls - m_currEvent.totalBalls;
				m_currEvent.totalBalls = newState.atBatBalls;
				m_currEvent.totalStrikes = newState.atBatStrikes;
			}
			else if(IsSameAtBat(m_oldState, newState) && IsEndOfCurrentAtBat(m_oldState, newState))
			{
				// If a batter strikes out we never get an update with 3 strikes on it
				// so check the play text
				if (newState.lastUpdate.Contains("struck out") || newState.lastUpdate.Contains("strikes out"))
				{
					// Set the strikes to the total for the team that WAS batting
					m_currEvent.totalStrikes = m_oldState.topOfInning ? m_oldState.awayStrikes.GetValueOrDefault() : m_oldState.homeStrikes.GetValueOrDefault();
					newStrikes = m_currEvent.totalStrikes - m_oldState.atBatStrikes;
				}
				else if (newState.lastUpdate.Contains("walk"))
				{
					m_currEvent.totalBalls = 4;
					m_currEvent.eventType = GameEventType.WALK;
					m_currEvent.isWalk = true;
					newBalls = m_currEvent.totalBalls - m_oldState.atBatBalls;
				}
				else if (newState.lastUpdate.Contains("with a pitch!"))
				{
					m_currEvent.eventType = GameEventType.HIT_BY_PITCH;
				}

			}
			else if(IsStartOfNextAtBat(m_oldState, newState, 2))
			{
				newStrikes = newState.atBatStrikes;
				newBalls = newState.atBatBalls;
			}
			// This else case should return so we can assume we are only covering one event below
			else
			{
				AddParsingError(m_currEvent, $"Event jumped to processing a different batter unexpectedly");
				return;
			}

			// Oops, we hit a gap, lets see if we can fill it in
			if(newStrikes + newBalls > 1)
			{
				// Error: We skipped *something*, we should log it
				AddFixedError(m_currEvent, $"A single update had more than one pitch, but we fixed it");
				// We can know for sure the state of the last strike.
				if (newState.lastUpdate.Contains("struck out") || newState.lastUpdate.Contains("strikes out"))
				{
					if (newState.lastUpdate.Contains("looking"))
					{
						m_currEvent.pitchesList.Add('C');
						newStrikes -= 1;
					}
					else if (newState.lastUpdate.Contains("swinging"))
					{
						m_currEvent.pitchesList.Add('S');
						newStrikes -= 1;
					}
				}
				// We can know for sure that the last pitch was a ball
				else if (newState.lastUpdate.Contains("walk"))
				{
					m_currEvent.pitchesList.Add('B');
					newBalls -= 1;
				}

				// Add the rest as unknowns
				for (int i = 0; i < newStrikes; i++)
				{
					m_currEvent.pitchesList.Add('K');
				}
				for (int i = 0; i < newBalls; i++)
				{
					m_currEvent.pitchesList.Add('A');
				}
			}
			else if (newStrikes == 1)
			{
				if (newState.lastUpdate.Contains("looking"))
				{
					m_currEvent.pitchesList.Add('C');
				}
				else if (newState.lastUpdate.Contains("swinging"))
				{
					m_currEvent.pitchesList.Add('S');
				}
				else if (newState.lastUpdate.Contains("Foul Ball"))
				{
					// Do nothing, fouls are handled at the end
				}
				else
				{
					m_currEvent.pitchesList.Add('K');
					AddFixedError(m_currEvent, $"We missed a single strike, but we fixed it");
				}
			} 
			else if (newBalls == 1)
			{
				m_currEvent.pitchesList.Add('B');
				if (!(newState.lastUpdate.Contains("Ball.") || newState.lastUpdate.Contains("walk.")))
				{
					AddFixedError(m_currEvent, $"We missed a single ball, but we fixed it");
				}
			}

			if (newState.lastUpdate.Contains("Foul Ball"))
			{
				m_currEvent.totalFouls++;
				m_currEvent.pitchesList.Add('F');
			}
		}

		/// <summary>
		/// Update outs (they're annoying)
		/// </summary>
		private void UpdateOuts(Game newState)
		{
			// If the inning suddenly changed, that means this play got all the rest of the outs
			// TODO: triple plays if implemented
			if (newState.topOfInning != m_oldState.topOfInning && m_oldState.halfInningOuts > 0)
			{
				m_currEvent.outsOnPlay = 3 - m_oldState.halfInningOuts;
			}
			else
			{
				m_currEvent.outsOnPlay = Math.Max(0, newState.halfInningOuts - m_oldState.halfInningOuts);
			}

			// Types of outs
			if (newState.lastUpdate.Contains("out") || newState.lastUpdate.Contains("sacrifice") || newState.lastUpdate.Contains("hit into a double play"))
			{
				if (newState.lastUpdate.Contains("strikes out") || newState.lastUpdate.Contains("struck out"))
				{
					m_currEvent.eventType = GameEventType.STRIKEOUT;
				}
				else if(newState.lastUpdate.Contains("sacrifice"))
				{
					m_currEvent.eventType = GameEventType.SACRIFICE;
				}
				else
				{
					m_currEvent.eventType = GameEventType.OUT;
				}
			}

		}

		/// <summary>
		/// Update hit information
		/// </summary>
		private void UpdateHits(Game newState)
		{
			// Handle RBIs
			if (!m_oldState.lastUpdate.Contains("steals"))
			{
				m_currEvent.runsBattedIn = newState.topOfInning ? newState.awayScore - m_oldState.awayScore : newState.homeScore - m_oldState.homeScore;
			}

			// Mark any kind of hit
			if (newState.lastUpdate.Contains("hits a") || newState.lastUpdate.Contains("hit a"))
			{
				m_currEvent.pitchesList.Add('X');
				m_currEvent.battedBallType = BattedBallType.UNKNOWN;
			}

			// Extremely basic single/double/triple/HR detection
			if (newState.lastUpdate.Contains("hits a Single"))
			{
				m_currEvent.basesHit = 1;
				m_currEvent.batterBaseAfterPlay = 1;
				m_currEvent.eventType = GameEventType.SINGLE;
			}
			else if (newState.lastUpdate.Contains("hits a Double"))
			{
				m_currEvent.basesHit = 2;
				m_currEvent.batterBaseAfterPlay = 2;
				m_currEvent.eventType = GameEventType.DOUBLE;
			}
			else if (newState.lastUpdate.Contains("hits a Triple"))
			{
				m_currEvent.basesHit = 3;
				m_currEvent.batterBaseAfterPlay = 3;
				m_currEvent.eventType = GameEventType.TRIPLE;
			}
			else if (newState.lastUpdate.Contains("home run") || newState.lastUpdate.Contains("grand slam"))
			{
				m_currEvent.basesHit = 4;
				m_currEvent.batterBaseAfterPlay = BASE_RUNNER_SCORED;
				m_currEvent.eventType = GameEventType.HOME_RUN;
				m_currEvent.battedBallType = BattedBallType.FLY;
			}

			if(newState.lastUpdate.Contains("ground"))
			{
				m_currEvent.battedBallType = BattedBallType.GROUNDER;
			}
			if(newState.lastUpdate.Contains("flyout"))
			{
				m_currEvent.battedBallType = BattedBallType.FLY;
			}
		}

		/// <summary>
		/// Should be called after UpdateOuts because fielder's choice overrides the generic OUT type
		/// </summary>
		private void UpdateFielding(Game newState)
		{
			// Sacrifice outs
			if(newState.lastUpdate.Contains("sacrifice fly"))
			{
				m_currEvent.isSacrificeFly = true;
			}
			else if (newState.lastUpdate.Contains("sacrifice"))
			{
				m_currEvent.isSacrificeHit = true;
			}

			// Double plays
			if (newState.lastUpdate.Contains("double play"))
			{
				m_currEvent.isDoublePlay = true;
			}

			// Triple plays
			if (newState.lastUpdate.Contains("triple play"))
			{
				m_currEvent.isTriplePlay = true;
			}

			// Fielder's choice
			// This has to go after out because it overrides it in case
			// a different batter was out.
			if (newState.lastUpdate.Contains("fielder's choice"))
			{
				m_currEvent.eventType = GameEventType.FIELDERS_CHOICE;
			}

			// Caught Stealing
			if (newState.lastUpdate.Contains("caught stealing"))
			{
				m_currEvent.eventType = GameEventType.CAUGHT_STEALING;
			}
		}


		private const int BASE_RUNNER_SCORED = 99;
		private const int BASE_RUNNER_OUT = 0;
		/// <summary>
		/// Update stuff around baserunning
		/// </summary>
		private void UpdateBaserunning(Game newState)
		{

			// Steals
			if (newState.lastUpdate.Contains("steals"))
			{
				m_currEvent.eventType = GameEventType.STOLEN_BASE;
				m_currEvent.isSteal = true;
				m_currEvent.isLastEventForPlateAppearance = false;
			}

			// If this play is known to be ending the inning or game
			if (m_currEvent.outsBeforePlay + m_currEvent.outsOnPlay >= 3 || newState.gameComplete)
			{
				// Baserunners should be exactly what we had in the last update
				// But create a new one if it's null
				if(m_currEvent.baseRunners == null)
				{
					m_currEvent.baseRunners = new List<GameEventBaseRunner>();
				}
				return;
			}
			else
			{
				// Clear to a new list every time we parse an update
				// Since runners can only move in cases where we emit, the last state should be correct
				m_currEvent.baseRunners = new List<GameEventBaseRunner>();
			}

			// Handle runners present in the new state and probably the old state too
			for(int i=0; i < newState.baseRunners.Count; i++)
			{
				string runnerId = newState.baseRunners[i];
				int baseIndex = newState.basesOccupied[i];

				GameEventBaseRunner runner = new GameEventBaseRunner();
				runner.runnerId = runnerId;

				// Add a new entry for this new baserunner
				if(!m_responsiblePitchers.ContainsKey(runnerId))
				{
					// Pitcher from the previous state must be responsible for this new baserunner
					m_responsiblePitchers[runnerId] = m_oldState.PitcherId;
				}

				runner.responsiblePitcherId = m_responsiblePitchers[runnerId];

				// We number home = 0, first = 1, second = 2, third = 3
				// Game updates have first = 0, second = 1, third = 2
				runner.baseAfterPlay = baseIndex + 1;

				// Find this runner's previous base in the old state
				bool found = false;
				for(int j=0; j < m_oldState.baseRunners.Count; j++)
				{
					if(m_oldState.baseRunners[j] == runnerId)
					{
						runner.baseBeforePlay = m_oldState.basesOccupied[j] + 1;
						found = true;
						if(runner.baseBeforePlay != runner.baseAfterPlay)
						{
							if(newState.lastUpdate.Contains("steals"))
							{
								runner.wasBaseStolen = true;
							}
							if(newState.lastUpdate.Contains("caught"))
							{
								runner.wasCaughtStealing = true;
							}
						}
					}
				}
				if(!found)
				{
					runner.baseBeforePlay = 0;
				}

				m_currEvent.baseRunners.Add(runner);
			}

			// Translate old baserunners into a more easily looped form
			Dictionary<int, string> oldBases = new Dictionary<int, string>();
			for (int i = 0; i < m_oldState.baseRunners.Count; i++)
			{
				oldBases[m_oldState.basesOccupied[i]] = m_oldState.baseRunners[i];
			}

			int newScore = m_oldState.topOfInning ? newState.awayScore : newState.homeScore;
			int oldScore = m_oldState.topOfInning ? m_oldState.awayScore : m_oldState.homeScore;
			int scoreDiff = newScore - oldScore;

			// Handle runners present in the old state but possibly not in the new ('cuz they scored)
			foreach (var kvp in oldBases.OrderByDescending(x => x.Key))
			{
				string runnerId = kvp.Value;
				int baseIndex = kvp.Key;

				bool found = false;
				for(int j=0; j < newState.baseRunners.Count; j++)
				{
					if(newState.baseRunners[j] == runnerId)
					{
						found = true;
					}
				}

				// If this old runner was not found and we have runs not attributed yet
				if (!found && scoreDiff > 0)
				{
					// One run accounted for
					scoreDiff--;
					GameEventBaseRunner runner = new GameEventBaseRunner();
					runner.runnerId = runnerId;
					if(m_responsiblePitchers.ContainsKey(runnerId))
					{
						runner.responsiblePitcherId = m_responsiblePitchers[runnerId];
					}
					else
					{
						runner.responsiblePitcherId = "";
						AddParsingError(m_currEvent, $"Couldn't find responsible pitcher for runner {runnerId} in update '{newState.lastUpdate}'");
					}

					runner.baseBeforePlay = baseIndex + 1;
					runner.baseAfterPlay = BASE_RUNNER_SCORED;
					if (newState.lastUpdate.Contains("steals"))
					{
						runner.wasBaseStolen = true;
					}
					m_currEvent.baseRunners.Add(runner);
				}
				else if (!found && m_currEvent.outsOnPlay > 0)
				{
					// Fine, he was out
					GameEventBaseRunner runner = new GameEventBaseRunner();
					runner.runnerId = runnerId;
					if (m_responsiblePitchers.ContainsKey(runnerId))
					{
						runner.responsiblePitcherId = m_responsiblePitchers[runnerId];
					}
					else
					{
						runner.responsiblePitcherId = "";
						AddParsingError(m_currEvent, $"Couldn't find responsible pitcher for runner {runnerId} in update '{newState.lastUpdate}'");
					}

					if (newState.lastUpdate.Contains("caught"))
					{
						runner.wasCaughtStealing = true;
					}

					runner.baseBeforePlay = baseIndex + 1;
					runner.baseAfterPlay = BASE_RUNNER_OUT;
					m_currEvent.baseRunners.Add(runner);
				}
				else if(found)
				{
					// Fine, he was found
				}
				else if(m_oldState.inning >= 8 && m_oldState.halfInningOuts == 2)
				{
					// In the case that the game-ending out just happened, we'll get an update still in the 9th inning but with outs back at 0 and gameComplete not yet true. Sigh.
					// For now, allow this in the 9th inning with 2 outs
					GameEventBaseRunner runner = new GameEventBaseRunner();
					runner.runnerId = runnerId;
					if (m_responsiblePitchers.ContainsKey(runnerId))
					{
						runner.responsiblePitcherId = m_responsiblePitchers[runnerId];
					}
					else
					{
						runner.responsiblePitcherId = "";
						AddParsingError(m_currEvent, $"Couldn't find responsible pitcher for runner {runnerId} in update '{newState.lastUpdate}'");
					}

					runner.baseBeforePlay = baseIndex + 1;
					runner.baseAfterPlay = baseIndex + 1;
					m_currEvent.baseRunners.Add(runner);
				}
				else
				{
					// What the hell else could have happened?
					AddParsingError(m_currEvent, $"Baserunner {runnerId} missing from base {baseIndex + 1}, but there were no outs and score went from {oldScore} to {newScore}");
				}
			}

			// Add the homering batter as a baserunner
			if (m_currEvent.eventType == GameEventType.HOME_RUN)
			{
				GameEventBaseRunner runner = new GameEventBaseRunner();
				runner.runnerId = newState.BatterId ?? m_oldState.BatterId;
				runner.responsiblePitcherId = newState.PitcherId;
				runner.baseBeforePlay = 0;
				runner.baseAfterPlay = BASE_RUNNER_SCORED;
				m_currEvent.baseRunners.Add(runner);
			}

			// Check for fixable errors with missed hits
			if(m_currEvent.eventType == GameEventType.UNKNOWN)
			{
				// Look to see if the batter got on base
				foreach(var runner in m_currEvent.baseRunners)
				{
					if(runner.runnerId == m_currEvent.batterId)
					{
						switch(runner.baseAfterPlay)
						{
							case 1:
								m_currEvent.eventType = GameEventType.SINGLE;
								m_currEvent.basesHit = 1;
								break;
							case 2:
								m_currEvent.eventType = GameEventType.DOUBLE;
								m_currEvent.basesHit = 2;
								break;
							case 3:
								m_currEvent.eventType = GameEventType.TRIPLE;
								m_currEvent.basesHit = 3;
								break;
							case BASE_RUNNER_SCORED:
								m_currEvent.eventType = GameEventType.HOME_RUN;
								m_currEvent.basesHit = 4;
								break;
						}

						// Some kind of hit!
						m_currEvent.pitchesList.Add('X');
						AddFixedError(m_currEvent, $"Found the batter apparently hit a {m_currEvent.eventType} without us seeing it, but fixed it.");
					}
				}
			}

			// Last thing - if we just changed innings, clear the responsible pitcher list
			// Note that we do this AFTER attributing baserunners who may have just done something on this play
			// and whose pitcher was from this old inning
			if (newState.inning != m_oldState.inning)
			{
				m_responsiblePitchers.Clear();
			}

		}

		/// <summary>
		/// Update metadata like the leadoff flag and lineupPosition
		/// </summary>
		private void UpdateLineupInfo(Game newState)
		{
			// Track first batter in each inning
			if(CanStartInning(newState))
			{
				StartInning(newState);
				m_currEvent.isLeadoff = true;
			}

			// Don't trust the batter counts when we change innings
			if(!IsNextHalfInning(m_oldState, newState) && !IsStartOfInningMessage(newState)){

				// Always attribute the event to the last pitcher involved
				// (but only if the half-inning didn't change, ugh)
				m_currEvent.pitcherId = newState.PitcherId;

				// Game updates have a batter count per team, so the lineup position is that % 9
				if (newState.topOfInning)
				{
					m_currEvent.lineupPosition = newState.awayTeamBatterCount % 9;
					if(m_currEvent.lineupPosition >= 0)
					{
						if (m_currEvent.batterId != null)
						{
							m_awayPlayerLineup[m_currEvent.lineupPosition] = m_currEvent.batterId;
						}
						else
						{
							AddFixedError(m_currEvent, $"Setting player based on lineup");
							m_currEvent.batterId = m_awayPlayerLineup[m_currEvent.lineupPosition];
						}
					}
				}
				else 
				{
					m_currEvent.lineupPosition = newState.homeTeamBatterCount % 9;
					if (m_currEvent.lineupPosition >= 0)
					{
						if (m_currEvent.batterId != null)
						{
							m_homePlayerLineup[m_currEvent.lineupPosition] = m_currEvent.batterId;
						}
						else
						{
							
							AddFixedError(m_currEvent, $"Setting player based on lineup");
							m_currEvent.batterId = m_homePlayerLineup[m_currEvent.lineupPosition];
						}
					}
				}
			}
		}


		private async Task<string> TryGetPlayerId(string name)
		{
			HttpResponseMessage response = await m_client.GetAsync($"playerIdsByName?name={HttpUtility.UrlEncode(name)}");

			if (response.IsSuccessStatusCode)
			{
				string strResponse = await response.Content.ReadAsStringAsync();
				var list = JsonSerializer.Deserialize<IEnumerable<Dictionary<string, string>>>(strResponse);

				if (list.Count() > 0)
					return list.First()["player_id"];
				else
					return "UNKNOWN";
			}
			else
			{
				return "UNKNOWN";
			}
		}

		private async Task TryPopulatePlayerId(Outcome o, string name)
		{
			o.entityId = await TryGetPlayerId(name);
		}

		private async Task CreateAndAddPlayerOutcome(string text, string type, string playerName)
		{
			Outcome o = new Outcome(text);
			o.eventType = type;
			await TryPopulatePlayerId(o, playerName);
			m_currEvent.outcomes.Add(o);
		}

		private static Regex debtRegex = new Regex(@"A Debt was collected.*(pitch|hitt)er (.+)! Replaced by (.+) The Instability (chains|spreads) to (.+)'s (.+)!");
		private static Regex incineRegex = new Regex(@".*incinerated.*(pitch|hitt)er (.+)! Replaced by (.+)");
		private static Regex peanutRegex = new Regex(@".*(pitch|hitt)er (.+) swallowed.*had an? (\w+) reaction!");
		private static Regex feedbackRegex = new Regex(@".*feedback.*\.\.\. (.+) is now up to bat\.");
		private static Regex feedbackBlockedRegex = new Regex(@"Reality begins to flicker...but (.+) resists! (.+) is affect");
		private static Regex teamReverbRegex = new Regex(@"Reverberations are at (\w+) levels! The (.+) lost (.+)");
		private static Regex playerReverbRegex = new Regex(@"Reverberations are at (\w+) levels! (.+) is now .*");
		private static Regex blooddrainRegex = new Regex(@"The Blooddrain gurgled! (.+) siphoned some of (.+)'s.*");
		private static Regex beanRegex = new Regex(@"(.+) hits (.+) with a pitch! (.+) is now Unstable!");
		private async Task UpdateOutcomes(Game newState)
		{
			var match = debtRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.DEBT_PAID, match.Groups[2].Value);
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.UNSTABLE_CHAINED, match.Groups[6].Value);
			}

			match = beanRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.BEANED_PITCHER, match.Groups[1].Value);
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.BEANED_HITTER, match.Groups[2].Value);
			}

			match = blooddrainRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.BLOOD_DRAIN_SIPHONER, match.Groups[1].Value);
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.BLOOD_DRAIN_VICTIM, match.Groups[2].Value);
			}

			match = feedbackRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				// Old player
				Outcome o = new Outcome(newState.lastUpdate);
				o.eventType = OutcomeType.FEEDBACK;
				o.entityId = m_currEvent.batterId;
				m_currEvent.outcomes.Add(o);

				// New player
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.FEEDBACK, match.Groups[1].Value);
			}

			match = feedbackBlockedRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.FEEDBACK_BLOCKED, match.Groups[1].Value);
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.FEEDBACK_BLOCKED, match.Groups[2].Value);
			}

			match = teamReverbRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				var teamName = match.Groups[2].Value;
				var status = match.Groups[3].Value;

				Outcome e = new Outcome(newState.lastUpdate);
				if (newState.homeTeamNickname == teamName)
				{
					e.entityId = newState.homeTeam;
				}
				else if(newState.awayTeamNickname == teamName)
				{
					e.entityId = newState.awayTeam;
				}
				else
				{
					AddParsingError(m_currEvent, $"Couldn't find which team ({teamName}) is reverberating!");
				}

				switch (status)
				{
					case "control of their pitchers!":
						e.eventType = OutcomeType.REVERB_PITCHERS;
						break;
					case "control of their hitters!":
						e.eventType = OutcomeType.REVERB_HITTERS;
						break;
					case "control of several players!":
						e.eventType = OutcomeType.REVERB_SEVERAL;
						break;
					case "all control!":
						e.eventType = OutcomeType.REVERB_ALL;
						break;
				}

				m_currEvent.outcomes.Add(e);
			}

			match = playerReverbRegex.Match(newState.lastUpdate);
			if(match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.REVERB_PLAYER, match.Groups[2].Value);
			}

			match = incineRegex.Match(newState.lastUpdate);
			if (match.Success)
			{
				await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.INCINERATION, match.Groups[2].Value);
			}

			match = peanutRegex.Match(newState.lastUpdate);
			if (match.Success)
			{
				string playerName = match.Groups[2].Value;

				if (match.Groups[3].Value == "yummy")
				{
					await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.PEANUT_GOOD, playerName);
				}
				else if (match.Groups[3].Value == "allergic")
				{
					await CreateAndAddPlayerOutcome(newState.lastUpdate, OutcomeType.PEANUT_BAD, playerName);
				}
			}
		}

		/// <summary>
		/// Final check for obvious errors in the event we're about to emit
		/// </summary>
		private void ErrorCheckBeforeEmit(GameEvent toEmit)
		{
			if(toEmit.baseRunners.Count > 0)
			{
				foreach(var runner in toEmit.baseRunners)
				{
					if(runner.runnerId == null)
					{
						AddParsingError(toEmit, $"Emitted an event with a NULL runnerId");
						runner.runnerId = "";
					}
				}
			}
			if(toEmit.batterId == null)
			{
				AddParsingError(toEmit, $"Emitted an event with NULL batterId");
			}
			if (toEmit.pitcherId == null)
			{
				AddParsingError(toEmit, $"Emitted an event with NULL pitcherId");
			}
			if(toEmit.eventType == GameEventType.UNKNOWN)
			{
				AddParsingError(toEmit, "Unknown event type");
			}
		}

		/// <summary>
		/// Call this with every game update for the game this parser is handling
		/// </summary>
		/// <param name="newState"></param>
		/// <param name="timeStamp"></param>
		/// <returns></returns>
		public async Task ParseGameUpdate(Game newState, DateTime timeStamp)
		{
			if(IsGameComplete)
			{
				m_discards++;
				return;
			}
			if(newState.Equals(m_oldState))
			{
				//Console.WriteLine($"Discarded update from game {newState._id} as a duplicate.");
				m_discards++;
				return;
			}
			else if(newState.gameId != m_oldState.gameId)
			{
				Console.WriteLine("ERROR: GameEventParser got an update for the wrong game!");
				m_discards++;
				return;
			}
			else
			{
				m_processed++;
			}

			if(m_currEvent == null)
			{
				m_currEvent = CreateNewGameEvent(newState, timeStamp);
			}

			CapturePlayerIds(newState);
			m_currEvent.lastPerceivedAt = timeStamp;

			// If we haven't found the batter for this event yet, try again
			if (m_currEvent.batterId == null)
			{
				m_currEvent.batterId = newState.BatterId;
			}

			// Presume this event will be last; steals can set this to false later
			m_currEvent.isLastEventForPlateAppearance = true;

			UpdateLineupInfo(newState);

			UpdateBallsAndStrikes(newState);

			UpdateOuts(newState);

			UpdateHits(newState);

			// Call after UpdateOuts
			UpdateFielding(newState);

			// Call after UpdateOuts
			UpdateBaserunning(newState);

			await UpdateOutcomes(newState);

			// Unknown or not currently handled event
			if(m_currEvent.eventType == null)
			{
				m_currEvent.eventType = GameEventType.UNKNOWN;
			}

			// Unsure if this is enough
			m_currEvent.isLastGameEvent = newState.gameComplete;
			IsGameComplete = newState.gameComplete;
			if(IsGameComplete && !m_sentGameComplete)
			{
				GameComplete?.Invoke(this, new GameCompleteEventArgs(m_gameEvents));
			}

			// Store original update text for reference
			m_currEvent.eventText.Add(newState.lastUpdate);

			// Cycle state
			m_oldState = newState;

			// If we had outs or hits or a walk or a steal, emit
			// OR IF THE GAME IS OVER, duh
			if(m_currEvent.outsOnPlay > 0 
				|| m_currEvent.basesHit > 0 
				|| m_currEvent.isSteal 
				|| m_currEvent.isWalk 
				|| m_currEvent.isLastGameEvent
				|| m_currEvent.eventType == GameEventType.HIT_BY_PITCH)
			{
				GameEvent emitted = m_currEvent;
				m_eventIndex++;

				if (m_currEvent.isSteal || m_currEvent.eventType == GameEventType.CAUGHT_STEALING)
				{
					// Start the next event in this state
					m_currEvent = CreateNewGameEvent(newState, timeStamp);
				}
				else
				{
					// Start the next event in the next state
					m_currEvent = null;
				}

				ErrorCheckBeforeEmit(emitted);
				m_gameEvents.Add(emitted);
			}

			if(IsGameComplete)
			{
				// Fire event
			}
		}
	}
}