using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZodiacBuddy.SmartCaseUtil
{
    public static class SmartCaseHelper
    {
        public static string SmartTitleCase(string input)
        {
            var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (Regex.IsMatch(words[i], @"^\d+(st|nd|rd|th)$", RegexOptions.IgnoreCase))
                {
                    words[i] = words[i].ToLowerInvariant();
                }
                else
                {
                    words[i] = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(words[i].ToLowerInvariant());
                }
            }

            return string.Join(' ', words);
        }
    }
}
