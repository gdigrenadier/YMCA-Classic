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
using System.Collections.Generic;
using System.Linq;
using OpenRA.FileSystem;
using OpenRA.Mods.Common.Lint;

namespace OpenRA.Mods.Common.UtilityCommands
{
	class CheckYaml : IUtilityCommand
	{
		string IUtilityCommand.Name { get { return "--check-yaml"; } }

		static int errors = 0;

		// mimic Windows compiler error format
		static void EmitError(string e)
		{
			Console.WriteLine("OpenRA.Utility(1,1): Error: {0}", e);
			++errors;
		}

		static void EmitWarning(string e)
		{
			Console.WriteLine("OpenRA.Utility(1,1): Warning: {0}", e);
		}

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return true;
		}

		[Desc("[MAPFILE]", "Check a mod or map for certain yaml errors.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			try
			{
				Log.AddChannel("debug", null);
				Log.AddChannel("perf", null);

				// bind some nonfatal error handling into FieldLoader, so we don't just *explode*.
				ObjectCreator.MissingTypeAction = s => EmitError("Missing Type: {0}".F(s));
				FieldLoader.UnknownFieldAction = (s, f) => EmitError("FieldLoader: Missing field `{0}` on `{1}`".F(s, f.Name));

				var maps = new List<(IReadWritePackage package, string map)>();
				if (args.Length < 2)
				{
					Console.WriteLine("Testing mod: {0}".F(modData.Manifest.Metadata.Title));

					// Run all rule checks on the default mod rules.
					CheckRules(modData, modData.DefaultRules);

					// Run all generic (not mod-level) checks here.
					foreach (var customPassType in modData.ObjectCreator.GetTypesImplementing<ILintPass>())
					{
						try
						{
							var customPass = (ILintPass)modData.ObjectCreator.CreateBasic(customPassType);
							customPass.Run(EmitError, EmitWarning, modData);
						}
						catch (Exception e)
						{
							EmitError("{0} failed with exception: {1}".F(customPassType, e));
						}
					}

					// Use all system maps for lint checking
					maps = modData.MapCache.EnumerateMapDirPackagesAndNames().ToList();
				}
				else
					maps.Add((new Folder(Platform.EngineDir), args[1]));

				foreach (var map in maps)
				{
					var testMap = new Map(modData, map.package.OpenPackage(map.map, modData.ModFiles));
					TestMap(testMap, modData);
				}

				if (errors > 0)
				{
					Console.WriteLine("Errors: {0}", errors);
					Environment.Exit(1);
				}
			}
			catch (Exception e)
			{
				EmitError("Failed with exception: {0}".F(e));
				Environment.Exit(1);
			}
		}

		void TestMap(Map map, ModData modData)
		{
			Console.WriteLine($"Testing map: {map.Title}");

			// Lint tests can't be trusted if the map rules are bogus
			// so report that problem then skip the tests
			if (map.InvalidCustomRules)
			{
				EmitError(map.InvalidCustomRulesException.ToString());
				return;
			}

			// Run all rule checks on the map if it defines custom rules.
			if (map.RuleDefinitions != null || map.VoiceDefinitions != null || map.WeaponDefinitions != null)
				CheckRules(modData, map.Rules, map);

			// Run all map-level checks here.
			foreach (var customMapPassType in modData.ObjectCreator.GetTypesImplementing<ILintMapPass>())
			{
				try
				{
					var customMapPass = (ILintMapPass)modData.ObjectCreator.CreateBasic(customMapPassType);
					customMapPass.Run(EmitError, EmitWarning, modData, map);
				}
				catch (Exception e)
				{
					EmitError($"{customMapPassType} failed with exception: {e}");
				}
			}
		}

		void CheckRules(ModData modData, Ruleset rules, Map map = null)
		{
			foreach (var customRulesPassType in modData.ObjectCreator.GetTypesImplementing<ILintRulesPass>())
			{
				try
				{
					var customRulesPass = (ILintRulesPass)modData.ObjectCreator.CreateBasic(customRulesPassType);
					customRulesPass.Run(EmitError, EmitWarning, modData, rules);
				}
				catch (Exception e)
				{
					EmitError("{0} failed with exception: {1}".F(customRulesPassType, e));
				}
			}
		}
	}
}
