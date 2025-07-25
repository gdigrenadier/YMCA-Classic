﻿#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.CA.Traits
{
	class GrantExternalConditionPowerCAInfo : SupportPowerInfo
	{
		[FieldLoader.Require]
		[Desc("The condition to apply. Must be included in the target actor's ExternalConditions list.")]
		public readonly string Condition = null;

		[Desc("Duration of the condition (in ticks). Set to 0 for a permanent condition.")]
		public readonly int Duration = 0;

		[FieldLoader.Require]
		[Desc("Size of the footprint of the affected area.")]
		public readonly CVec Dimensions = CVec.Zero;

		[FieldLoader.Require]
		[Desc("Actual footprint. Cells marked as x will be affected.")]
		public readonly string Footprint = string.Empty;

		[Desc("Sound to instantly play at the targeted area.")]
		public readonly string OnFireSound = null;

		[Desc("Player stances which condition can be applied to.")]
		public readonly PlayerRelationship ValidRelationships = PlayerRelationship.Ally;

		[SequenceReference]
		[Desc("Sequence to play for granting actor when activated.",
			"This requires the actor to have the WithSpriteBody trait or one of its derivatives.")]
		public readonly string Sequence = "active";

		[Desc("Cursor to display when there are no units to apply the condition in range.")]
		public readonly string BlockedCursor = "move-blocked";

		[WeaponReference]
		[FieldLoader.Require]
		public readonly string Weapon = "";

		[Desc("Delay between activation and explosion")]
		public readonly int ActivationDelay = 10;

		[Desc("Altitude above terrain below which to explode. Zero effectively deactivates airburst.")]
		public readonly WDist AirburstAltitude = WDist.Zero;

		[GrantedConditionReference]
		[Desc("A condition to apply while active.")]
		public readonly string ActiveCondition = null;

		[Desc("Duration of the Active condition (in ticks). Set to 0 for a permanent condition.")]
		public readonly int ActiveDuration = 50;

		public WeaponInfo WeaponInfo { get; private set; }

		public override object Create(ActorInitializer init) { return new GrantExternalConditionPowerCA(init.Self, this); }
		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
		}
	}

	class GrantExternalConditionPowerCA : SupportPower, ITick, ISync, INotifyCreated
	{
		readonly GrantExternalConditionPowerCAInfo info;
		readonly char[] footprint;
		int activeToken = Actor.InvalidConditionToken;
		IConditionTimerWatcher[] watchers;

		[Sync]
		public int Ticks { get; private set; }

		public GrantExternalConditionPowerCA(Actor self, GrantExternalConditionPowerCAInfo info)
			: base(self, info)
		{
			this.info = info;
			footprint = info.Footprint.Where(c => !char.IsWhiteSpace(c)).ToArray();
			Ticks = info.ActiveDuration;
		}

		protected override void Created(Actor self)
		{
			watchers = self.TraitsImplementing<IConditionTimerWatcher>().Where(Notifies).ToArray();

			base.Created(self);
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.OrderGenerator = new SelectConditionTarget(Self.World, order, manager, this);
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			if (!string.IsNullOrEmpty(info.ActiveCondition) && activeToken == Actor.InvalidConditionToken)
			{
				Ticks = info.Duration;
				activeToken = self.GrantCondition(info.ActiveCondition);
			}

			var wsb = self.TraitOrDefault<WithSpriteBody>();
			if (wsb != null && wsb.DefaultAnimation.HasSequence(info.Sequence))
				wsb.PlayCustomAnimation(self, info.Sequence);

			Game.Sound.Play(SoundType.World, info.OnFireSound, order.Target.CenterPosition);

			foreach (var a in UnitsInRange(self.World.Map.CellContaining(order.Target.CenterPosition)))
				a.TraitsImplementing<ExternalCondition>()
					.FirstOrDefault(t => t.Info.Condition == info.Condition && t.CanGrantCondition(a, self))
					?.GrantCondition(a, self, info.Duration);

			if (info.Weapon != null)
			{
				var targetPosition = order.Target.CenterPosition + new WVec(WDist.Zero, WDist.Zero, info.AirburstAltitude);

				Action detonateWeapon = () => self.World.AddFrameEndTask(w => info.WeaponInfo.Impact(Target.FromPos(targetPosition), self));

				self.World.AddFrameEndTask(w => w.Add(new DelayedAction(info.ActivationDelay, detonateWeapon)));
			}
		}

		public IEnumerable<Actor> UnitsInRange(CPos xy)
		{
			var tiles = CellsMatching(xy, footprint, info.Dimensions);
			var units = new List<Actor>();
			foreach (var t in tiles)
				units.AddRange(Self.World.ActorMap.GetActorsAt(t));

			return units.Distinct().Where(a =>
			{
				if (!info.ValidRelationships.HasStance(a.Owner.RelationshipWith(Self.Owner)))
					return false;

				return a.TraitsImplementing<ExternalCondition>()
					.Any(t => t.Info.Condition == info.Condition && t.CanGrantCondition(a, Self));
			});
		}

		void RevokeCondition(Actor self)
		{
			if (activeToken != Actor.InvalidConditionToken)
				activeToken = self.RevokeCondition(activeToken);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled && activeToken != Actor.InvalidConditionToken)
				RevokeCondition(self);

			if (IsTraitPaused || IsTraitDisabled)
				return;

			foreach (var w in watchers)
				w.Update(info.Duration, Ticks);

			if (activeToken == Actor.InvalidConditionToken)
				return;

			if (--Ticks < 1)
				RevokeCondition(self);
		}

		bool Notifies(IConditionTimerWatcher watcher) { return watcher.Condition == info.ActiveCondition; }

		class SelectConditionTarget : OrderGenerator
		{
			readonly GrantExternalConditionPowerCA power;
			readonly char[] footprint;
			readonly CVec dimensions;
			readonly Sprite tile;
			readonly SupportPowerManager manager;
			readonly string order;

			public SelectConditionTarget(World world, string order, SupportPowerManager manager, GrantExternalConditionPowerCA power)
			{
				// Clear selection if using Left-Click Orders
				if (Game.Settings.Game.UseClassicMouseStyle)
					manager.Self.World.Selection.Clear();

				this.manager = manager;
				this.order = order;
				this.power = power;
				footprint = power.info.Footprint.Where(c => !char.IsWhiteSpace(c)).ToArray();
				dimensions = power.info.Dimensions;
				tile = world.Map.Rules.Sequences.GetSequence("overlay", "target-select").GetSprite(0);
			}

			protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
			{
				world.CancelInputMode();
				if (mi.Button == MouseButton.Left && power.UnitsInRange(cell).Any())
					yield return new Order(order, manager.Self, Target.FromCell(world, cell), false) { SuppressVisualFeedback = true };
			}

			protected override void Tick(World world)
			{
				// Cancel the OG if we can't use the power
				if (!manager.Powers.TryGetValue(order, out var p) || !p.Active || !p.Ready)
					world.CancelInputMode();
			}

			protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }

			protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world)
			{
				var xy = wr.Viewport.ViewToWorld(Viewport.LastMousePos);
				foreach (var unit in power.UnitsInRange(xy))
				{
					var decorations = unit.TraitsImplementing<ISelectionDecorations>().FirstEnabledTraitOrDefault();
					if (decorations != null)
						foreach (var d in decorations.RenderSelectionAnnotations(unit, wr, Color.Red))
							yield return d;
				}
			}

			protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world)
			{
				var xy = wr.Viewport.ViewToWorld(Viewport.LastMousePos);
				var pal = wr.Palette(TileSet.TerrainPaletteInternalName);

				foreach (var t in power.CellsMatching(xy, footprint, dimensions))
					yield return new SpriteRenderable(tile, wr.World.Map.CenterOfCell(t), WVec.Zero, -511, pal, 1f, true, true);
			}

			protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
			{
				return power.UnitsInRange(cell).Any() ? power.info.Cursor : power.info.BlockedCursor;
			}
		}
	}
}
