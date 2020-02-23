﻿using OpenAPI.Player;

namespace OpenAPI.Events.Player
{
	/// <summary>
	/// 	Dispatched whenever an <see cref="OpenPlayer"/> says something in chat
	/// </summary>
	public class PlayerChatEvent : PlayerEvent
	{
		private string _original;
		
		/// <summary>
		/// 	The message sent by the player
		/// </summary>
		public string Message { get; set; }
		public PlayerChatEvent(OpenPlayer player, string message) : base(player)
		{
			_original = message;
			Message = message;
		}
	}
}
