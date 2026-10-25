using System;
using UnityEngine;

namespace SailEmblems
{
    /// <summary>What is painted on one sail. Also what gets saved.</summary>
    [Serializable]
    public struct EmblemState : IEquatable<EmblemState>
    {
        public string Id;      // null = plain sail
        public int Color;      // Palette index
        public float Size;     // emblem: its height as a share of the sail height; pattern: its scale up the sail (1 = as drawn)
        public float Stretch;  // pattern: its scale across the sail (1 = as drawn); emblems ignore it
        public float Offset;   // up (+) / down (-) as a share of the sail height
        public float OffsetX;  // to the right (+) / left (-) as a share of the sail width
        public bool Mirror;    // painted the other way round (left and right swapped)
        public bool AlongFoot; // pattern: laid along the sail's foot (the boom of a gaff) instead of level

        public bool IsPattern => Id != null && Id.StartsWith("p:", StringComparison.Ordinal);

        public static EmblemState Default(string id, Sail sail = null)
        {
            var st = new EmblemState { Id = id, Color = 0, Size = 0.5f, Stretch = 1f };
            if (st.IsPattern)
            {
                st.Size = 1f;
                // a gaff's or a lug's boom is the base of the pattern (Кэп); a lateen's foot is a free edge
                st.AlongFoot = sail != null && (sail.category == SailCategory.gaff ||
                                                sail.sailName != null && sail.sailName.IndexOf("lug", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return st;
        }

        public bool IsEmpty => string.IsNullOrEmpty(Id);

        public bool Equals(EmblemState o)
        {
            if (IsEmpty || o.IsEmpty)
                return IsEmpty == o.IsEmpty;
            return Id == o.Id && Color == o.Color && Mirror == o.Mirror && AlongFoot == o.AlongFoot &&
                   P(Size) == P(o.Size) && P(Stretch) == P(o.Stretch) && P(Offset) == P(o.Offset) && P(OffsetX) == P(o.OffsetX);
        }

        private static int P(float v) => Mathf.RoundToInt(v * 100f);

        public override bool Equals(object obj) => obj is EmblemState o && Equals(o);
        public override int GetHashCode() => IsEmpty ? 0 : Id.GetHashCode() ^ Color;

        // "id|color|size|offset|flags|offsetX|stretch" in hundredths - compact, readable, survives new emblems being added.
        // 0.1 wrote no flags, 0.2-0.3 nothing after them.
        public string Encode()
        {
            if (IsEmpty) return "";
            int flags = (Mirror ? 1 : 0) | (AlongFoot ? 2 : 0);
            return Id + "|" + Color + "|" + P(Size) + "|" + P(Offset) + "|" + flags + "|" + P(OffsetX) + "|" + P(Stretch);
        }

        public static bool TryDecode(string s, out EmblemState st)
        {
            st = default(EmblemState);
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split('|');
            if (p.Length < 4 || p[0].Length == 0) return false;
            if (!int.TryParse(p[1], out int c) || !int.TryParse(p[2], out int size) || !int.TryParse(p[3], out int off)) return false;
            int flags = 0, offX = 0, stretch = 100;
            if (p.Length > 4) int.TryParse(p[4], out flags);
            if (p.Length > 5) int.TryParse(p[5], out offX);
            if (p.Length > 6) int.TryParse(p[6], out stretch);
            st = new EmblemState
            {
                Id = p[0], Color = c, Size = size / 100f, Offset = Mathf.Clamp(off / 100f, -0.5f, 0.5f),
                OffsetX = Mathf.Clamp(offX / 100f, -0.5f, 0.5f), Stretch = Mathf.Clamp(stretch / 100f, 0.2f, 4f),
                Mirror = (flags & 1) != 0, AlongFoot = (flags & 2) != 0,
            };
            // before 0.4 patterns had no scale (their saved size was the emblem default, unused)
            if (st.IsPattern && p.Length <= 6) st.Size = 1f;
            st.Size = Mathf.Clamp(st.Size, st.IsPattern ? 0.2f : 0.05f, st.IsPattern ? 4f : 1f);
            return true;
        }

        public override string ToString() => IsEmpty ? "plain" : Encode();
    }
}
