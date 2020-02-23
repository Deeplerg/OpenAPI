﻿using OpenAPI.World;

namespace OpenAPI.Events.Level
{
	/// <summary>
	/// 	Dispatched when an entity gets removed from an <see cref="OpenLevel"/>
	/// </summary>
	public class LevelEntityRemovedEvent : LevelEvent
	{
		/// <summary>
		/// 	The entity that got removed
		/// </summary>
		public MiNET.Entities.Entity Entity { get; }
		public LevelEntityRemovedEvent(OpenLevel world, MiNET.Entities.Entity entity) : base(world)
		{
			Entity = entity;
		}
	}
}
