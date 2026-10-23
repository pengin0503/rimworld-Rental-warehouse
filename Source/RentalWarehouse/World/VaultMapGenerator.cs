using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RentalWarehouse
{
    /// <summary>
    /// Builds the vault compound from the site record: same layout for the same seed; only vaults not destroyed, only
    /// crates still at the site, and only the guards' remaining points. Nothing is restocked (spec 7).
    /// </summary>
    public class GenStep_VaultCompound : GenStep
    {
        public override int SeedPart => 0x52574753;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (map.Parent is VaultSite site && site.Record != null) VaultMapGenerator.Generate(site, map, site.Record);
        }
    }

    public static class VaultMapGenerator
    {
        public const int CompoundSize = 21;

        public static GenStepDef GenStepDef => DefDatabase<GenStepDef>.GetNamed("RW_VaultCompound");

        public static CellRect Compound(Map map) => CellRect.CenteredOn(map.Center, CompoundSize / 2);

        public static void Generate(VaultSite site, Map map, SiteRecord record)
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null || map == null) return;
            var faction = Ids.Faction(record.FactionId) ?? site?.Faction;
            Rand.PushState(record.Seed);
            try
            {
                var rect = Compound(map);
                Clear(map, rect);
                var wallStuff = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite") ?? ThingDefOf.Steel;
                foreach (var cell in rect.EdgeCells)
                {
                    if (cell == DoorCell(rect)) continue;
                    Spawn(ThingDefOf.Wall, wallStuff, cell, map, faction);
                }
                Spawn(ThingDefOf.Door, wallStuff, DoorCell(rect), map, faction);
                foreach (var cell in rect) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);

                // Vaults stand in a row along the north wall, one cell apart.
                for (int i = 0; i < record.Vaults.Count; i++)
                {
                    var state = record.Vaults[i];
                    var pos = VaultCell(rect, i);
                    if (state.Destroyed) continue;
                    var vault = (Building_Vault)ThingMaker.MakeThing(RWDefOf.RW_Vault);
                    vault.siteId = record.SiteId;
                    vault.vaultKey = state.VaultId;
                    vault.SetFaction(faction);
                    GenSpawn.Spawn(vault, pos, map);
                }

                // Crates outside a locked vault: ejected from unlocked/destroyed vaults or dropped earlier.
                foreach (var id in SiteService.CratesAtSite(world.State, record).ToList())
                {
                    var c = world.State.Cargo[id];
                    var vaultState = record.Vaults.FirstOrDefault(v => v.VaultId == c.ContainerId);
                    if (vaultState != null && !vaultState.Unlocked && !vaultState.Destroyed) continue;
                    int index = vaultState == null ? 0 : record.Vaults.IndexOf(vaultState);
                    var crate = CargoAdapter.MakeCrate(c);
                    crate.SetForbidden(false, warnOnFail: false);
                    GenPlace.TryPlaceThing(crate, VaultCell(rect, index) + IntVec3.South * 3, map, ThingPlaceMode.Near);
                }

                SpawnGuards(map, rect, faction, record.GuardPointsRemaining);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static IntVec3 DoorCell(CellRect rect) => new IntVec3(rect.CenterCell.x, 0, rect.minZ);

        public static IntVec3 VaultCell(CellRect rect, int index) => new IntVec3(rect.minX + 3 + index * 4, 0, rect.maxZ - 3);

        private static void Clear(Map map, CellRect rect)
        {
            foreach (var cell in rect.ExpandedBy(1))
            {
                if (!cell.InBounds(map)) continue;
                foreach (var t in cell.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                }
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
            }
        }

        private static void Spawn(ThingDef def, ThingDef stuff, IntVec3 cell, Map map, Faction faction)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff : null);
            thing.SetFaction(faction);
            GenSpawn.Spawn(thing, cell, map);
        }

        private static void SpawnGuards(Map map, CellRect rect, Faction faction, decimal points)
        {
            if (faction == null || points <= 0m) return;
            if ((float)points < faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)) return;
            var parms = new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                tile = map.Tile,
                faction = faction,
                points = (float)points,
                inhabitants = true,
                generateFightersOnly = true,
            };
            var pawns = PawnGroupMakerUtility.GeneratePawns(parms).ToList();
            foreach (var p in pawns) GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(rect.CenterCell, map, 6), map);
            if (pawns.Count > 0) LordMaker.MakeNewLord(faction, new LordJob_DefendBase(faction, rect.CenterCell, 25000), map, pawns);
        }

        /// <summary>Before the map is discarded: remember which crates lie where and how many guard points survive.</summary>
        public static void WriteBack(VaultSite site, Map map)
        {
            var world = WarehouseWorldComponent.Instance;
            var record = site.Record;
            if (world == null || record == null || map == null) return;

            float guard = 0f;
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Faction != null && p.Faction == site.Faction && !p.Dead && !p.Downed && !p.IsPrisoner) guard += p.kindDef.combatPower;
            }
            record.GuardPointsRemaining = (decimal)guard;

            foreach (var kv in world.AllCrates.ToList())
            {
                var crate = kv.Value;
                if (crate.MapHeld != map || !world.State.Cargo.TryGetValue(kv.Key, out var c) || c.SiteId != record.SiteId) continue;
                if (c.Custody == CargoCustody.Player)
                {
                    // Taken but left behind on the site: it stays the site's (spec 7 "持ち出した箱" only).
                    c.Custody = CargoCustody.Depositor;
                    c.OwnerFactionId = c.OriginalOwnerFactionId ?? record.FactionId;
                    c.Stolen = false;
                }
                CargoAdapter.RemoveCrate(crate);
            }
        }
    }
}
