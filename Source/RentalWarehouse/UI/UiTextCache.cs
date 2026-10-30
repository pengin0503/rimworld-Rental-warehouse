using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// The management tab draws every frame and asked for each entry's text twice (once to measure, once to draw), rebuilding
    /// long translated strings each time. An entry's text and its measured height are kept for a quarter of a second instead.
    /// Real time is used so that edits made while the game is paused still show up.
    /// </summary>
    public static class UiTextCache
    {
        private const float RefreshPerSecond = 4f;

        private sealed class Entry
        {
            public int Stamp = -1;
            public string Text;
            public float Width = -1f;
            public float Height;
        }

        private static readonly ConditionalWeakTable<object, Entry> entries = new ConditionalWeakTable<object, Entry>();

        private static int Stamp => (int)(Time.realtimeSinceStartup * RefreshPerSecond);

        /// <summary>The text for <paramref name="owner"/> (a ledger object such as an offer), rebuilt at most four times a second.</summary>
        public static string Text(object owner, Func<string> build)
        {
            var entry = entries.GetOrCreateValue(owner);
            int stamp = Stamp;
            if (entry.Text == null || entry.Stamp != stamp)
            {
                entry.Text = build();
                entry.Stamp = stamp;
                entry.Width = -1f;
            }
            return entry.Text;
        }

        /// <summary>Height of <paramref name="owner"/>'s text at the given width; <see cref="Text"/> must have been asked for first.</summary>
        public static float Height(object owner, float width)
        {
            var entry = entries.GetOrCreateValue(owner);
            if (entry.Text == null) return 0f;
            if (entry.Width != width)
            {
                entry.Height = Text_CalcHeight(entry.Text, width);
                entry.Width = width;
            }
            return entry.Height;
        }

        private static float Text_CalcHeight(string text, float width) => Verse.Text.CalcHeight(text, width);
    }
}
