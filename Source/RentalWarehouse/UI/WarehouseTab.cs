using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Management tab (spec 8): offers, contracts, accounts and vault intel.</summary>
    public class WarehouseTab : MainTabWindow
    {
        public enum Page
        {
            Offers,
            Contracts,
            Accounts,
            Vaults,
        }

        private static Page current = Page.Offers;
        private Vector2 scroll;

        /// <summary>Fits 1280x720 at UI scale 1.0 (A14).</summary>
        public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(980f, UI.screenWidth - 40f), Mathf.Min(640f, UI.screenHeight - 80f));

        public static readonly Dictionary<Page, Func<Rect, Vector2, Vector2>> Views = new Dictionary<Page, Func<Rect, Vector2, Vector2>>
        {
            [Page.Offers] = OfferView.Draw,
            [Page.Contracts] = ContractView.Draw,
            [Page.Accounts] = AccountingView.Draw,
            [Page.Vaults] = VaultInfoView.Draw,
        };

        public static void Open()
        {
            var def = DefDatabase<MainButtonDef>.GetNamedSilentFail("RW_Warehouse");
            if (def != null) Find.MainTabsRoot.SetCurrentTab(def);
        }

        public override void DoWindowContents(Rect inRect)
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null) return;
            if (world.LedgerUnreadable)
            {
                Widgets.Label(inRect, "RW_Diag_LedgerUnreadable".Translate(""));
                return;
            }

            var header = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            DrawHeader(header, world);

            var tabs = new List<TabRecord>();
            foreach (Page page in Enum.GetValues(typeof(Page)))
            {
                if (!Views.ContainsKey(page)) continue;
                var p = page;
                tabs.Add(new TabRecord(("RW_Tab_" + p).Translate(), () => { current = p; scroll = Vector2.zero; }, current == p));
            }
            var body = new Rect(inRect.x, header.yMax + 40f, inRect.width, inRect.height - header.height - 40f);
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs, 1, null);
            if (!Views.TryGetValue(current, out var view)) current = Page.Offers;
            scroll = Views[current](body.ContractedBy(10f), scroll);
        }

        private static void DrawHeader(Rect rect, WarehouseWorldComponent world)
        {
            var left = rect.LeftPart(0.6f);
            Text.Font = GameFont.Medium;
            Widgets.Label(left, "RW_TabTitle".Translate());
            Text.Font = GameFont.Small;
            var right = rect.RightPart(0.4f);
            string status = world.State.BusinessOpen ? "RW_BusinessIsOpen".Translate() : "RW_BusinessIsClosed".Translate();
            if (Widgets.ButtonText(right, status))
            {
                world.SetBusinessOpen(!world.State.BusinessOpen);
            }
        }

        /// <summary>Draws a vertical list of variable-height entries inside a scroll view.</summary>
        public static Vector2 ScrollList<T>(Rect rect, Vector2 scroll, IList<T> items, Func<T, float, float> measure, Action<T, Rect> draw, string emptyKey)
        {
            if (items.Count == 0)
            {
                Widgets.Label(rect, emptyKey.Translate());
                return scroll;
            }
            float width = rect.width - 16f;
            float total = 0f;
            var heights = new float[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                heights[i] = measure(items[i], width);
                total += heights[i] + 8f;
            }
            var view = new Rect(0f, 0f, width, total);
            Widgets.BeginScrollView(rect, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rect(0f, y, width, heights[i]);
                Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.04f));
                draw(items[i], r.ContractedBy(4f));
                y += heights[i] + 8f;
            }
            Widgets.EndScrollView();
            return scroll;
        }

        /// <summary>Height of word-wrapped text at the current font.</summary>
        public static float TextHeight(string text, float width) => Text.CalcHeight(text, width);
    }
}
