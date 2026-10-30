using System.Collections.Generic;
using System.Globalization;
using RentalWarehouse.Core;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Editor for the trust rank table (spec 7). Every valid edit is stored in the mod settings at once; an invalid
    /// table shows its reason and is not stored. The multiplier column is an input aid: it fills the four totals.
    /// </summary>
    public class RankTableDialog : Window
    {
        private const float RowHeight = 30f;
        private const float MaxValue = 100000000f;
        private static readonly float[] Widths = { 0.17f, 0.11f, 0.11f, 0.12f, 0.12f, 0.12f, 0.12f, 0.13f };
        private static readonly string[] Headers =
        {
            "RW_Rank_Col_Rank", "RW_Rank_Col_Points", "RW_Rank_Col_Mult", "RW_Rank_Col_NormalMin",
            "RW_Rank_Col_NormalMax", "RW_Rank_Col_HighMin", "RW_Rank_Col_HighMax", "RW_Rank_Col_Crates",
        };

        private readonly RentalWarehouseSettings settings;
        private readonly List<RankRow> rows;
        private readonly string[,] numberBuffers = new string[TrustRanks.Count, 8];
        private readonly string[] multiplierText = new string[TrustRanks.Count];
        private Vector2 scroll;
        private string error;

        public RankTableDialog(RentalWarehouseSettings settings)
        {
            this.settings = settings;
            rows = settings.RankRows();
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            for (int i = 0; i < rows.Count; i++) multiplierText[i] = MultiplierText(rows[i]);
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(980f, UI.screenWidth - 40f), Mathf.Min(600f, UI.screenHeight - 80f));

        public override void PreClose()
        {
            base.PreClose();
            settings.Write();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "RW_Rank_Title".Translate());
            Text.Font = GameFont.Small;
            float hintHeight = Text.CalcHeight("RW_Rank_Hint".Translate(), inRect.width);
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, hintHeight), "RW_Rank_Hint".Translate());

            float y = inRect.y + 38f + hintHeight;
            DrawHeader(new Rect(inRect.x, y, inRect.width, RowHeight));
            y += RowHeight;

            float footer = 110f;
            var outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - footer);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, TrustRanks.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            bool changed = false;
            for (int i = 0; i < rows.Count; i++) changed |= DrawRow(new Rect(0f, i * RowHeight, viewRect.width, RowHeight), i);
            Widgets.EndScrollView();
            if (changed) Commit();

            y = outRect.yMax + 4f;
            if (error != null)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 26f), error.Translate());
                GUI.color = Color.white;
            }
            else
            {
                var risky = new List<string>();
                for (int i = 0; i < rows.Count; i++)
                {
                    if (TrustRanks.ExceedsPerCrateLimit(rows[i])) risky.Add(TrustAdapter.RankName(i));
                }
                if (risky.Count > 0)
                {
                    GUI.color = new Color(1f, 0.75f, 0.3f);
                    Widgets.Label(new Rect(inRect.x, y, inRect.width, 26f), "RW_Rank_Warn".Translate(string.Join(", ", risky)));
                    GUI.color = Color.white;
                }
            }
            if (Widgets.ButtonText(new Rect(inRect.x, y + 30f, 260f, 30f), "RW_Rank_Reset".Translate())) Reset();
        }

        private void DrawHeader(Rect rect)
        {
            for (int c = 0; c < Headers.Length; c++) Widgets.Label(Cell(rect, c), Headers[c].Translate());
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
        }

        private bool DrawRow(Rect rect, int i)
        {
            var row = rows[i];
            bool changed = false;
            Widgets.Label(Cell(rect, 0), TrustAdapter.RankName(i));

            if (i == 0) Widgets.Label(Cell(rect, 1), "0");
            else changed |= IntField(Cell(rect, 1), i, 1, row.RequiredPoints, v => row.RequiredPoints = v);

            changed |= MultiplierField(Cell(rect, 2), i);

            bool totalsChanged = false;
            totalsChanged |= IntField(Cell(rect, 3), i, 3, (int)row.NormalMin, v => row.NormalMin = v);
            totalsChanged |= IntField(Cell(rect, 4), i, 4, (int)row.NormalMax, v => row.NormalMax = v);
            totalsChanged |= IntField(Cell(rect, 5), i, 5, (int)row.HighRiskMin, v => row.HighRiskMin = v);
            totalsChanged |= IntField(Cell(rect, 6), i, 6, (int)row.HighRiskMax, v => row.HighRiskMax = v);
            if (totalsChanged) multiplierText[i] = MultiplierText(row);

            changed |= totalsChanged;
            changed |= IntField(Cell(rect, 7), i, 7, row.MaxCrates, v => row.MaxCrates = v);
            return changed;
        }

        private bool IntField(Rect rect, int i, int column, int current, System.Action<int> set)
        {
            int value = current;
            string buffer = numberBuffers[i, column];
            Widgets.TextFieldNumeric(rect.ContractedBy(2f), ref value, ref buffer, 0f, MaxValue);
            numberBuffers[i, column] = buffer;
            if (value == current) return false;
            set(value);
            return true;
        }

        private bool MultiplierField(Rect rect, int i)
        {
            string text = multiplierText[i] ?? "";
            string edited = Widgets.TextField(rect.ContractedBy(2f), text);
            if (string.IsNullOrEmpty(edited))
            {
                GUI.color = Color.gray;
                Widgets.Label(rect.ContractedBy(4f), "RW_Rank_Custom".Translate());
                GUI.color = Color.white;
            }
            if (edited == text) return false;
            multiplierText[i] = edited;
            if (!decimal.TryParse(edited, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) || m <= 0m || m > 1000m) return false;
            TrustRanks.ApplyMultiplier(rows[i], m);
            for (int column = 3; column <= 6; column++) numberBuffers[i, column] = null;
            return true;
        }

        private void Commit()
        {
            error = TrustRanks.Validate(rows);
            if (error == null) settings.SetRankRows(rows);
        }

        private void Reset()
        {
            rows.Clear();
            rows.AddRange(TrustRanks.Default());
            for (int i = 0; i < rows.Count; i++)
            {
                multiplierText[i] = MultiplierText(rows[i]);
                for (int column = 0; column < 8; column++) numberBuffers[i, column] = null;
            }
            error = null;
            settings.SetRankRows(rows);
        }

        private static string MultiplierText(RankRow row)
        {
            var m = TrustRanks.MultiplierOf(row);
            return m.HasValue ? m.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
        }

        private static Rect Cell(Rect row, int column)
        {
            float x = row.x;
            for (int c = 0; c < column; c++) x += Widths[c] * row.width;
            return new Rect(x, row.y, Widths[column] * row.width, row.height);
        }
    }
}
