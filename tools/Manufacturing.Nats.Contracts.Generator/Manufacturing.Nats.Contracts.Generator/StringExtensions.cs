using System.Globalization;
using System.Text.RegularExpressions;

namespace Manufacturing.Nats.Contracts.Generator;

public static class StringExtensions
{
    extension(string value)
    {
        public string Indent(int indent = 4)
        {
            if (indent <= 0 || string.IsNullOrEmpty(value))
            {
                return value;
            }

            return new string(' ', indent) + value;
        }

        public string DurableNameToClassName()
        {
            // remove :: separators
            return string.Join("", [.. value.Where(v => v != ':')]).ToPascalCase();
        }

        // ----- COPIED FROM YamlDotNet (MIT License)
        // Repository: https://github.com/aaubry/YamlDotNet
        // Source: https://github.com/aaubry/YamlDotNet/blob/master/YamlDotNet/Helpers/StringExtensions.cs
        private static string ToCamelOrPascalCase(string str, Func<char, char> firstLetterTransform)
        {
            if (string.IsNullOrEmpty(str))
            {
                return str;
            }

            var text = Regex.Replace(str, "([_\\-])(?<char>[a-z])",
                                     match => match.Groups["char"].Value.ToUpperInvariant(),
                                     RegexOptions.IgnoreCase);
            return firstLetterTransform(text[0]) + text.Substring(1);
        }


        /// <summary>
        /// Convert the string with underscores (this_is_a_test) or hyphens (this-is-a-test) to 
        /// camel case (thisIsATest). Camel case is the same as Pascal case, except the first letter
        /// is lowercase.
        /// </summary>
        /// <param name="str">String to convert</param>
        /// <returns>Converted string</returns>
        public string ToCamelCase()
        {
            return ToCamelOrPascalCase(value, char.ToLowerInvariant);
        }

        /// <summary>
        /// Convert the string with underscores (this_is_a_test) or hyphens (this-is-a-test) to 
        /// pascal case (ThisIsATest). Pascal case is the same as camel case, except the first letter
        /// is uppercase.
        /// </summary>
        /// <param name="str">String to convert</param>
        /// <returns>Converted string</returns>
        public string ToPascalCase()
        {
            return ToCamelOrPascalCase(value, char.ToUpperInvariant);
        }

        /// <summary>
        /// Convert the string from camelcase (thisIsATest) to a hyphenated (this-is-a-test) or 
        /// underscored (this_is_a_test) string
        /// </summary>
        /// <param name="valueString to convert</param>
        /// <param name="separator">Separator to use between segments</param>
        /// <returns>Converted string</returns>
        public string FromCamelCase(string separator)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            // Ensure first letter is always lowercase
            value = char.ToLower(value[0], CultureInfo.InvariantCulture) + value.Substring(1);

            value = Regex.Replace(value.ToCamelCase(), "(?<char>[A-Z])",
                                match => separator + match.Groups["char"].Value.ToLowerInvariant());
            return value;
        }
        // ----- END OF COPIED FROM YamlDotNet
    }
}