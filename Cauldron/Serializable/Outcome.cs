﻿using Cauldron.Serializable;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cauldron
{
	
	public static class OutcomeType
	{
		public static string INCINERATION = "INCINERATION";
		public static string PEANUT_GOOD = "PEANUT_GOOD";
		public static string PEANUT_BAD = "PEANUT_BAD";
		public static string FEEDBACK = "FEEDBACK";
		public static string FEEDBACK_BLOCKED = "FEEDBACK_BLOCKED";
		public static string REVERB_PITCHERS = "REVERB_PITCHERS";
		public static string REVERB_HITTERS = "REVERB_HITTERS";
		public static string REVERB_SEVERAL = "REVERB_SEVERAL";
		public static string REVERB_ALL = "REVERB_ALL";
		public static string REVERB_PLAYER = "REVERB_PLAYER";
		public static string BLOOD_DRAIN_VICTIM = "BLOOD_DRAIN_VICTIM";
		public static string BLOOD_DRAIN_SIPHONER = "BLOOD_DRAIN_SIPHONER";
	}

	/// <summary>
	/// Player events
	/// </summary>
	public class Outcome
	{
		public Outcome(string text)
		{
			originalText = text;
		}

		[DbNullValue("UNKNOWN")]
		public string entityId { get; set; }
		public string eventType { get; set; }
		public string originalText { get; set; }
	}
}
