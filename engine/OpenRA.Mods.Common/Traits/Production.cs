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

using System;
using System.Linq;
using System.Collections.Generic;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("This unit has access to build queues.")]
	public class ProductionInfo : PausableConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("e.g. Infantry, Vehicles, Aircraft, Buildings")]
		public readonly string[] Produces = { };

		[Desc("should the units be produced into cargo")]
		public readonly bool ProduceIntoCargo = false;

		[Desc("should the units be produced into the cargo of the cargo")]
		public readonly bool ProduceIntoCargoOfCargo = false;

		public override object Create(ActorInitializer init) { return new Production(init, this); }
	}

	public class Production : PausableConditionalTrait<ProductionInfo>
	{
		readonly ProductionInfo info;
		RallyPoint rp;

		public string Faction { get; private set; }

		public Production(ActorInitializer init, ProductionInfo info)
			: base(info)
		{
			this.info = info;
			Faction = init.GetValue<FactionInit, string>(init.Self.Owner.Faction.InternalName);
		}

		protected override void Created(Actor self)
		{
			rp = self.TraitOrDefault<RallyPoint>();
			base.Created(self);
		}

		public virtual void DoProduction(Actor self, ActorInfo producee, ExitInfo exitinfo, string productionType, TypeDictionary inits)
		{
			var exit = CPos.Zero;
			var exitLocations = new List<CPos>();

			// Clone the initializer dictionary for the new actor
			var td = new TypeDictionary();
			foreach (var init in inits)
				td.Add(init);

			if (exitinfo != null && self.OccupiesSpace != null && producee.HasTraitInfo<IOccupySpaceInfo>())
			{
				exit = self.Location + exitinfo.ExitCell;
				var spawn = self.CenterPosition + exitinfo.SpawnOffset;
				var to = self.World.Map.CenterOfCell(exit);

				WAngle initialFacing;
				if (!exitinfo.Facing.HasValue)
				{
					var delta = to - spawn;
					if (delta.HorizontalLengthSquared == 0)
					{
						var fi = producee.TraitInfoOrDefault<IFacingInfo>();
						initialFacing = fi != null ? fi.GetInitialFacing() : WAngle.Zero;
					}
					else
						initialFacing = delta.Yaw;
				}
				else
					initialFacing = exitinfo.Facing.Value;

				exitLocations = rp != null && rp.Path.Count > 0 ? rp.Path : new List<CPos> { exit };

				td.Add(new LocationInit(exit));
				td.Add(new CenterPositionInit(spawn));
				td.Add(new FacingInit(initialFacing));
				if (exitinfo != null)
					td.Add(new CreationActivityDelayInit(exitinfo.ExitDelay));
			}

			self.World.AddFrameEndTask(w =>
			{
				// Here we check if production should go into the cargo of a passenger and if so, if cargo trait exists and if there is space to do so
				if (info.ProduceIntoCargoOfCargo && self.TraitOrDefault<Cargo>() != null && self.TraitOrDefault<Cargo>().Passengers.Any(p => p.TraitOrDefault<Cargo>().HasSpace(producee.TraitInfo<PassengerInfo>().Weight)))
				{
					var newUnit = self.World.CreateActor(false, producee.Name, td);

					foreach (var p in self.TraitOrDefault<Cargo>().Passengers)
					{
						if (p.TraitOrDefault<Cargo>().HasSpace(producee.TraitInfo<PassengerInfo>().Weight)) {
							p.TraitOrDefault<Cargo>().Load(p, newUnit);

							if (!self.IsDead)
								foreach (var t in self.TraitsImplementing<INotifyProduction>())
									t.UnitProduced(self, newUnit, exit);

							var notifyOthers = self.World.ActorsWithTrait<INotifyOtherProduction>();
							foreach (var notify in notifyOthers)
								notify.Trait.UnitProducedByOther(notify.Actor, self, newUnit, productionType, td);

							break;
						}
					}
				}
				// Here we check if production should go into the cargo of the producer and if so, if cargo trait exists and if there is space to do so
				else if (info.ProduceIntoCargo && self.TraitOrDefault<Cargo>() != null && self.TraitOrDefault<Cargo>().HasSpace(producee.TraitInfo<PassengerInfo>().Weight))
				{
					var newUnit = self.World.CreateActor(false, producee.Name, td);

					self.TraitOrDefault<Cargo>().Load(self, newUnit);

					if (!self.IsDead)
						foreach (var t in self.TraitsImplementing<INotifyProduction>())
							t.UnitProduced(self, newUnit, exit);

					var notifyOthers = self.World.ActorsWithTrait<INotifyOtherProduction>();
					foreach (var notify in notifyOthers)
						notify.Trait.UnitProducedByOther(notify.Actor, self, newUnit, productionType, td);
				}
				// this is actually the good old production
				else
				{
					var newUnit = self.World.CreateActor(producee.Name, td);

					var move = newUnit.TraitOrDefault<IMove>();
					if (exitinfo != null && move != null)
						foreach (var cell in exitLocations)
							newUnit.QueueActivity(new AttackMoveActivity(newUnit, () => move.MoveTo(cell, 1, evaluateNearestMovableCell: true, targetLineColor: Color.OrangeRed)));

					if (!self.IsDead)
						foreach (var t in self.TraitsImplementing<INotifyProduction>())
							t.UnitProduced(self, newUnit, exit);

					var notifyOthers = self.World.ActorsWithTrait<INotifyOtherProduction>();
					foreach (var notify in notifyOthers)
						notify.Trait.UnitProducedByOther(notify.Actor, self, newUnit, productionType, td);
				}
			});
		}

		public delegate void ManipulateActorCallback(Actor unit);

		public virtual void DoProduction(Actor self, ActorInfo producee, ExitInfo exitinfo, string productionType, TypeDictionary inits, ManipulateActorCallback onSpawnCallback)
		{
			var exit = CPos.Zero;
			var exitLocations = new List<CPos>();

			// Clone the initializer dictionary for the new actor
			var td = new TypeDictionary();
			foreach (var init in inits)
				td.Add(init);

			if (exitinfo != null && self.OccupiesSpace != null && producee.HasTraitInfo<IOccupySpaceInfo>())
			{
				exit = self.Location + exitinfo.ExitCell;
				var spawn = self.CenterPosition + exitinfo.SpawnOffset;
				var to = self.World.Map.CenterOfCell(exit);

				WAngle initialFacing;
				if (!exitinfo.Facing.HasValue)
				{
					var delta = to - spawn;
					if (delta.HorizontalLengthSquared == 0)
					{
						var fi = producee.TraitInfoOrDefault<IFacingInfo>();
						initialFacing = fi != null ? fi.GetInitialFacing() : WAngle.Zero;
					}
					else
						initialFacing = delta.Yaw;
				}
				else
					initialFacing = exitinfo.Facing.Value;

				exitLocations = rp != null && rp.Path.Count > 0 ? rp.Path : new List<CPos> { exit };

				td.Add(new LocationInit(exit));
				td.Add(new CenterPositionInit(spawn));
				td.Add(new FacingInit(initialFacing));
				if (exitinfo != null)
					td.Add(new CreationActivityDelayInit(exitinfo.ExitDelay));
			}

			self.World.AddFrameEndTask(w =>
			{
				var newUnit = self.World.CreateActor(producee.Name, td);

				onSpawnCallback(newUnit);

				var move = newUnit.TraitOrDefault<IMove>();
				if (exitinfo != null && move != null)
					foreach (var cell in exitLocations)
						newUnit.QueueActivity(new AttackMoveActivity(newUnit, () => move.MoveTo(cell, 1, evaluateNearestMovableCell: true, targetLineColor: Color.OrangeRed)));

				if (!self.IsDead)
					foreach (var t in self.TraitsImplementing<INotifyProduction>())
						t.UnitProduced(self, newUnit, exit);

				var notifyOthers = self.World.ActorsWithTrait<INotifyOtherProduction>();
				foreach (var notify in notifyOthers)
					notify.Trait.UnitProducedByOther(notify.Actor, self, newUnit, productionType, td);
			});
		}

		protected virtual Exit SelectExit(Actor self, ActorInfo producee, string productionType, Func<Exit, bool> p)
		{
			if (rp == null || rp.Path.Count == 0)
				return self.RandomExitOrDefault(self.World, productionType, p);

			return self.NearestExitOrDefault(self.World.Map.CenterOfCell(rp.Path[0]), productionType, p);
		}

		protected Exit SelectExit(Actor self, ActorInfo producee, string productionType)
		{
			return SelectExit(self, producee, productionType, e => CanUseExit(self, producee, e.Info));
		}

		public virtual bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue)
		{
			if (IsTraitDisabled || IsTraitPaused || Reservable.IsReserved(self))
				return false;

			// Pick a spawn/exit point pair
			var exit = SelectExit(self, producee, productionType);
			if (exit != null || self.OccupiesSpace == null || !producee.HasTraitInfo<IOccupySpaceInfo>())
			{
				DoProduction(self, producee, exit?.Info, productionType, inits);
				return true;
			}

			return false;
		}

		public virtual bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue, ManipulateActorCallback onSpawnCallback)
		{
			if (IsTraitDisabled || IsTraitPaused || Reservable.IsReserved(self))
				return false;

			// Pick a spawn/exit point pair
			var exit = SelectExit(self, producee, productionType);
			if (exit != null || self.OccupiesSpace == null || !producee.HasTraitInfo<IOccupySpaceInfo>())
			{
				DoProduction(self, producee, exit?.Info, productionType, inits, onSpawnCallback);
				return true;
			}

			return false;
		}

		static bool CanUseExit(Actor self, ActorInfo producee, ExitInfo s)
		{
			var mobileInfo = producee.TraitInfoOrDefault<MobileInfo>();

			self.NotifyBlocker(self.Location + s.ExitCell);

			return mobileInfo == null ||
				mobileInfo.CanEnterCell(self.World, self, self.Location + s.ExitCell, ignoreActor: self);
		}
	}
}
