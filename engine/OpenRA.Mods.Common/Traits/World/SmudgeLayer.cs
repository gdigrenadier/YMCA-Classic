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
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public struct MapSmudge
	{
		public string Type;
		public int Depth;
	}

	[Desc("Attach this to the world actor.", "Order of the layers defines the Z sorting.")]
	public class SmudgeLayerInfo : TraitInfo
	{
		public readonly string Type = "Scorch";

		[Desc("Sprite sequence name")]
		public readonly string Sequence = "scorch";

		[Desc("Chance of smoke rising from the ground")]
		public readonly int SmokeChance = 0;

		[Desc("By how much (in each direction) can the smoke appearance offset stray from the center of the cell?",
			"Note: Limit this to half a cell for square and 1/3 a cell for isometric cells to avoid straying into neighbour cells.")]
		public readonly WDist MaxSmokeOffsetDistance = WDist.Zero;

		[Desc("Smoke sprite image name")]
		public readonly string SmokeImage = null;

		[SequenceReference(nameof(SmokeImage))]
		[Desc("Smoke sprite sequences randomly chosen from")]
		public readonly string[] SmokeSequences = { };

		[PaletteReference]
		public readonly string SmokePalette = "effect";

		[PaletteReference]
		public readonly string Palette = TileSet.TerrainPaletteInternalName;

		[FieldLoader.LoadUsing("LoadInitialSmudges")]
		public readonly Dictionary<CPos, MapSmudge> InitialSmudges;

		public static object LoadInitialSmudges(MiniYaml yaml)
		{
			var nd = yaml.ToDictionary();
			var smudges = new Dictionary<CPos, MapSmudge>();
			if (nd.TryGetValue("InitialSmudges", out var smudgeYaml))
			{
				foreach (var node in smudgeYaml.Nodes)
				{
					try
					{
						var cell = FieldLoader.GetValue<CPos>("key", node.Key);
						var parts = node.Value.Value.Split(',');
						var type = parts[0];
						var depth = FieldLoader.GetValue<int>("depth", parts[1]);
						smudges.Add(cell, new MapSmudge { Type = type, Depth = depth });
					}
					catch { }
				}
			}

			return smudges;
		}

		public override object Create(ActorInitializer init) { return new SmudgeLayer(init.Self, this); }
	}

	public class SmudgeLayer : IRenderOverlay, IWorldLoaded, ITickRender, INotifyActorDisposing
	{
		struct Smudge
		{
			public string Type;
			public int Depth;
			public ISpriteSequence Sequence;
		}

		public readonly SmudgeLayerInfo Info;
		readonly Dictionary<CPos, Smudge> tiles = new Dictionary<CPos, Smudge>();
		readonly Dictionary<CPos, Smudge> dirty = new Dictionary<CPos, Smudge>();
		readonly Dictionary<string, ISpriteSequence> smudges = new Dictionary<string, ISpriteSequence>();
		readonly World world;
		readonly bool hasSmoke;

		TerrainSpriteLayer render;
		bool disposed;

		public SmudgeLayer(Actor self, SmudgeLayerInfo info)
		{
			Info = info;
			world = self.World;
			hasSmoke = !string.IsNullOrEmpty(info.SmokeImage) && info.SmokeSequences.Any();

			var sequenceProvider = world.Map.Rules.Sequences;
			var types = sequenceProvider.Sequences(Info.Sequence);
			foreach (var t in types)
				smudges.Add(t, sequenceProvider.GetSequence(Info.Sequence, t));
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			var sprites = smudges.Values.SelectMany(v => Exts.MakeArray(v.Length, x => v.GetSprite(x))).ToList();
			var sheet = sprites[0].Sheet;
			var blendMode = sprites[0].BlendMode;

			if (sprites.Any(s => s.Sheet != sheet))
				throw new InvalidDataException("Resource sprites span multiple sheets. Try loading their sequences earlier.");

			if (sprites.Any(s => s.BlendMode != blendMode))
				throw new InvalidDataException("Smudges specify different blend modes. "
					+ "Try using different smudge types for smudges that use different blend modes.");

			render = new TerrainSpriteLayer(w, wr, sheet, blendMode, wr.Palette(Info.Palette), w.Type != WorldType.Editor);

			// Add map smudges
			foreach (var kv in Info.InitialSmudges)
			{
				var s = kv.Value;
				if (!smudges.ContainsKey(s.Type))
					continue;

				var seq = smudges[s.Type];
				var smudge = new Smudge
				{
					Type = s.Type,
					Depth = s.Depth,
					Sequence = seq
				};

				tiles.Add(kv.Key, smudge);
				render.Update(kv.Key, seq, s.Depth);
			}
		}

		public void AddSmudge(CPos loc)
		{
			if (!world.Map.Contains(loc))
				return;

			if (hasSmoke && Game.CosmeticRandom.Next(0, 100) <= Info.SmokeChance)
			{
				var maxOffsetDistance = Info.MaxSmokeOffsetDistance.Length;
				var x = world.LocalRandom.Next(-maxOffsetDistance, maxOffsetDistance);
				var y = world.LocalRandom.Next(-maxOffsetDistance, maxOffsetDistance);
				var offset = new WVec(x, y, 0);
				world.AddFrameEndTask(w => w.Add(new SpriteEffect(
					w.Map.CenterOfCell(loc) + offset, w, Info.SmokeImage, Info.SmokeSequences.Random(w.LocalRandom), Info.SmokePalette)));
			}

			// A null Sequence indicates a deleted smudge.
			if ((!dirty.ContainsKey(loc) || dirty[loc].Sequence == null) && !tiles.ContainsKey(loc))
			{
				// No smudge; create a new one
				var st = smudges.Keys.Random(Game.CosmeticRandom);
				dirty[loc] = new Smudge { Type = st, Depth = 0, Sequence = smudges[st] };
			}
			else
			{
				// Existing smudge; make it deeper
				// A null Sequence indicates a deleted smudge.
				var tile = dirty.ContainsKey(loc) && dirty[loc].Sequence != null ? dirty[loc] : tiles[loc];
				var maxDepth = smudges[tile.Type].Length;
				if (tile.Depth < maxDepth - 1)
					tile.Depth++;

				dirty[loc] = tile;
			}
		}

		public void RemoveSmudge(CPos loc)
		{
			if (!world.Map.Contains(loc))
				return;

			var tile = dirty.ContainsKey(loc) ? dirty[loc] : default(Smudge);

			// Setting Sequence to null to indicate a deleted smudge.
			tile.Sequence = null;
			dirty[loc] = tile;
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			var remove = new List<CPos>();
			foreach (var kv in dirty)
			{
				if (!world.FogObscures(kv.Key))
				{
					// A null Sequence
					if (kv.Value.Sequence == null)
					{
						tiles.Remove(kv.Key);
						render.Clear(kv.Key);
					}
					else
					{
						var smudge = kv.Value;
						tiles[kv.Key] = smudge;
						render.Update(kv.Key, smudge.Sequence, smudge.Depth);
					}

					remove.Add(kv.Key);
				}
			}

			foreach (var r in remove)
				dirty.Remove(r);
		}

		void IRenderOverlay.Render(WorldRenderer wr)
		{
			render.Draw(wr.Viewport);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			render.Dispose();
			disposed = true;
		}
	}
}
