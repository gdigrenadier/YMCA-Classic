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

using System.Collections.Generic;

namespace OpenRA.Mods.Common.UpdateRules.Rules
{
	public class RenameStances : UpdateRule
    {
		public override string Name { get { return "Renamed player 'Stances' to 'Relationships'."; } }
		public override string Description
		{
			get
			{
				return "'Stances' in regards to a player have been renamed to 'Relationships'.\n" +
					"The yaml values did not change.";
			}
		}

		readonly (string TraitName, string OldName, string NewName)[] traits =
		{
			("Disguise", "ValidRelationships", "ValidRelationships"),
			("Infiltrates", "ValidRelationships", "ValidRelationships"),
			("AcceptsDeliveredCash", "ValidRelationships", "ValidRelationships"),
			("AcceptsDeliveredExperience", "ValidRelationships", "ValidRelationships"),
			("Armament", "TargetStances", "TargetRelationships"),
			("Armament", "ForceTargetStances", "ForceTargetRelationships"),
			("AutoTargetPriority", "ValidRelationships", "ValidRelationships"),
			("CaptureManagerBotModule", "CapturableStances", "CapturableRelationships"),
			("Capturable", "ValidRelationships", "ValidRelationships"),
			("Captures", "PlayerExperienceStances", "PlayerExperienceRelationships"),
			("ProximityExternalCondition", "ValidRelationships", "ValidRelationships"),
			("CreatesShroud", "ValidRelationships", "ValidRelationships"),
			("Demolition", "TargetStances", "TargetRelationships"),
			("Demolition", "ForceTargetStances", "ForceTargetRelationships"),
			("EngineerRepair", "ValidRelationships", "ValidRelationships"),
			("GivesBounty", "ValidRelationships", "ValidRelationships"),
			("GivesExperience", "ValidRelationships", "ValidRelationships"),
			("JamsMissiles", "DeflectionStances", "DeflectionRelationships"),
			("FrozenUnderFog", "AlwaysVisibleStances", "AlwaysVisibleRelationships"),
			("HiddenUnderShroud", "AlwaysVisibleStances", "AlwaysVisibleRelationships"),
			("HiddenUnderFog", "AlwaysVisibleStances", "AlwaysVisibleRelationships"),
			("AppearsOnRadar", "ValidRelationships", "ValidRelationships"),
			("CashTricklerBar", "DisplayStances", "DisplayRelationships"),
			("SupportPowerChargeBar", "DisplayStances", "DisplayRelationships"),
			("WithAmmoPipsDecoration", "ValidRelationships", "ValidRelationships"),
			("WithCargoPipsDecoration", "ValidRelationships", "ValidRelationships"),
			("WithDecoration", "ValidRelationships", "ValidRelationships"),
			("WithHarvesterPipsDecoration", "ValidRelationships", "ValidRelationships"),
			("WithNameTagDecoration", "ValidRelationships", "ValidRelationships"),
			("WithResourceStoragePipsDecoration", "ValidRelationships", "ValidRelationships"),
			("WithTextDecoration", "ValidRelationships", "ValidRelationships"),
			("WithRangeCircle", "ValidRelationships", "ValidRelationships"),
			("RevealOnDeath", "RevealForStances", "RevealForRelationships"),
			("RevealOnFire", "RevealForStancesRelativeToTarget", "RevealForRelationships"),
			("RevealsMap", "ValidRelationships", "ValidRelationships"),
			("RevealsShroud", "ValidRelationships", "ValidRelationships"),
			("VoiceAnnouncement", "ValidRelationships", "ValidRelationships"),
			("GrantExternalConditionPower", "ValidRelationships", "ValidRelationships"),
			("NukePower", "CameraStances", "CameraRelationships"),
			("NukePower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("AttackOrderPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("ChronoshiftPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("DropPodsPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("GpsPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("GrantPrerequisiteChargeDrainPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("IonCannonPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("AirstrikePower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("GrantExternalConditionPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("ParatroopersPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("ProduceActorPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("SpawnActorPower", "DisplayTimerStances", "DisplayTimerRelationships"),
			("TooltipDescription", "ValidRelationships", "ValidRelationships")
		};

		public override IEnumerable<string> UpdateActorNode(ModData modData, MiniYamlNode actorNode)
		{
			foreach (var field in traits)
				foreach (var traitNode in actorNode.ChildrenMatching(field.TraitName))
					traitNode.RenameChildrenMatching(field.OldName, field.NewName);

			yield break;
		}

		public override IEnumerable<string> UpdateWeaponNode(ModData modData, MiniYamlNode weaponNode)
		{
			foreach (var projectileNode in weaponNode.ChildrenMatching("Projectile"))
				projectileNode.RenameChildrenMatching("ValidBounceBlockerStances", "ValidBounceBlockerRelationships");

			foreach (var warheadNode in weaponNode.ChildrenMatching("Warhead"))
				warheadNode.RenameChildrenMatching("ValidRelationships", "ValidRelationships");

			yield break;
		}
	}
}
