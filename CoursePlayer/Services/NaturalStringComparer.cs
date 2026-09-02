using System;
using System.Collections.Generic;

namespace CoursePlayer.Services
{
    /// <summary>
    /// Compares strings in natural order (e.g., "file2.txt" comes before "file10.txt").
    /// </summary>
    public class NaturalStringComparer : IComparer<string>
    {
        /// <summary>
        /// Gets the singleton instance of the natural string comparer.
        /// </summary>
        public static NaturalStringComparer Instance { get; } = new NaturalStringComparer();

        /// <summary>
        /// Compares two strings using natural ordering.
        /// </summary>
        /// <param name="x">The first string to compare.</param>
        /// <param name="y">The second string to compare.</param>
        /// <returns>
        /// A negative integer if x is less than y, zero if they are equal, or a positive integer if x is greater than y.
        /// </returns>
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                // Skip leading spaces
                while (i < x.Length && char.IsWhiteSpace(x[i])) i++;
                while (j < y.Length && char.IsWhiteSpace(y[j])) j++;

                // If both are at the end, break
                if (i >= x.Length && j >= y.Length) break;

                // Get the next chunk of x and y
                string chunkX = GetChunk(x, ref i);
                string chunkY = GetChunk(y, ref j);

                int result;
                if (int.TryParse(chunkX, out int numX) && int.TryParse(chunkY, out int numY))
                {
                    result = numX.CompareTo(numY);
                }
                else
                {
                    result = string.Compare(chunkX, chunkY, StringComparison.OrdinalIgnoreCase);
                }

                if (result != 0) return result;
            }
            return 0;
        }

        /// <summary>
        /// Gets the next chunk (either digits or non-digits) from the string starting at the current index.
        /// </summary>
        /// <param name="s">The string to parse.</param>
        /// <param name="index">The current index, which will be updated to the position after the chunk.</param>
        /// <returns>The chunk as a string.</returns>
        private string GetChunk(string s, ref int index)
        {
            if (index >= s.Length) return string.Empty;

            if (char.IsDigit(s[index]))
            {
                int start = index;
                while (index < s.Length && char.IsDigit(s[index])) index++;
                return s.Substring(start, index - start);
            }
            else
            {
                int start = index;
                while (index < s.Length && !char.IsDigit(s[index])) index++;
                return s.Substring(start, index - start);
            }
        }
    }
}
