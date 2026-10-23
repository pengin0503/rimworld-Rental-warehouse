using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Vault intel shown before a raid (spec 7): owner, size, loot estimate, guard band, deadlines.</summary>
    public static class VaultInfoView
    {
        private const float ButtonWidth = 150f;

        public static Vector2 Draw(Rect rect, Vector2 scroll)
        {
            var world = WarehouseWorldComponent.Instance;
            var sites = world.State.Sites.Where(s => !s.Closed).ToList();
            return WarehouseTab.ScrollList(rect, scroll, sites,
                (s, w) => WarehouseTab.TextHeight(Describe(world, s), w - ButtonWidth - 12f) + 8f,
                (s, r) =>
                {
                    Widgets.Label(new Rect(r.x, r.y, r.width - ButtonWidth - 12f, r.height), Describe(world, s));
                    var site = VaultDiscovery.FindSite(s.SiteId);
                    if (site != null && Widgets.ButtonText(new Rect(r.xMax - ButtonWidth, r.y, ButtonWidth, 32f), "RW_ShowOnWorld".Translate()))
                    {
                        CameraJumper.TryJumpAndSelect(site);
                    }
                }, "RW_NoSites");
        }

        /// <summary>Rounded estimate only: exact contents are unknown until the crates are opened.</summary>
        public static string Describe(WarehouseWorldComponent world, SiteRecord s)
        {
            var sb = new StringBuilder();
            var faction = Ids.Faction(s.FactionId);
            sb.AppendLine("RW_SiteHeader".Translate(faction?.Name ?? "?", ("RW_Tier_" + s.Tier).Translate()));
            if (faction != null && !faction.HostileTo(Faction.OfPlayer)) sb.AppendLine("RW_SiteNotHostile".Translate());
            decimal loot = SiteService.CratesAtSite(world.State, s).Sum(id => world.State.Cargo[id].DeclaredValue);
            decimal rounded = decimal.Round(loot / 500m) * 500m;
            sb.AppendLine("RW_SiteLoot".Translate(SiteService.CratesAtSite(world.State, s).Count(), rounded.ToString("0")));
            sb.AppendLine("RW_SiteGuards".Translate(GuardBand(s.GuardPointsRemaining).Translate()));
            sb.AppendLine("RW_SiteVaults".Translate(s.Vaults.Count, ((int)VaultSiteParameters.UnlockWork(s.Tier)).ToStringTicksToPeriod()));
            long now = Ids.Now;
            if (s.FirstEntryTick == null)
            {
                sb.Append("RW_SiteExpires".Translate(((int)System.Math.Max(0, s.CreatedTick + VaultSiteParameters.UnvisitedLifetime - now)).ToStringTicksToPeriod()));
            }
            else
            {
                sb.Append("RW_SiteWithdraw".Translate(((int)System.Math.Max(0, s.FirstEntryTick.Value + VaultSiteParameters.WithdrawalAfterFirstEntry - now)).ToStringTicksToPeriod()));
            }
            return sb.ToString();
        }

        public static string GuardBand(decimal points)
        {
            if (points <= 0m) return "RW_Guards_None";
            if (points < 500m) return "RW_Guards_Light";
            if (points < 1500m) return "RW_Guards_Medium";
            return "RW_Guards_Heavy";
        }
    }
}
