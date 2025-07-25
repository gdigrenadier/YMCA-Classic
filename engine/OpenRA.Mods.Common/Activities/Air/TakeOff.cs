#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class TakeOff : Activity
	{
		readonly Aircraft aircraft;
		readonly IMove move;

		public TakeOff(Actor self)
		{
			aircraft = self.Trait<Aircraft>();
			move = self.Trait<IMove>();
		}

		protected override void OnFirstRun(Actor self)
		{
			if (aircraft.ForceLanding)
				return;

			if (self.World.Map.DistanceAboveTerrain(aircraft.CenterPosition).Length >= aircraft.Info.MinAirborneAltitude)
				return;

			// We are taking off, so remove influence in ground cells.
			aircraft.RemoveInfluence();

			var shouldStart = aircraft.Info.AudibleThroughFog || (!self.World.ShroudObscures(self.CenterPosition) && !self.World.FogObscures(self.CenterPosition));
			var sound = aircraft.Info.TakeoffSounds.RandomOrDefault(Game.CosmeticRandom);

			if (aircraft.Info.TakeoffSounds.Length > 0)
				Game.Sound.Play(SoundType.World, sound, self.CenterPosition, shouldStart ? aircraft.Info.Volume : 0f);
		}

		public override bool Tick(Actor self)
		{
			// Refuse to take off if it would land immediately again.
			if (aircraft.ForceLanding)
			{
				Cancel(self);
				return true;
			}

			var dat = self.World.Map.DistanceAboveTerrain(aircraft.CenterPosition);
			if (dat < aircraft.Info.CruiseAltitude)
			{
				// If we're a VTOL, rise before flying forward
				if (aircraft.Info.VTOL)
				{
					Fly.VerticalTakeOffOrLandTick(self, aircraft, aircraft.Facing, aircraft.Info.CruiseAltitude);
					return false;
				}

				Fly.FlyTick(self, aircraft, aircraft.Facing, aircraft.Info.CruiseAltitude);
				return false;
			}

			return true;
		}
	}
}
