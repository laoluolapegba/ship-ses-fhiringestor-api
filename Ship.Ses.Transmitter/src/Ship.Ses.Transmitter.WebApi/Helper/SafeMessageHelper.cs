namespace Ship.Ses.Ingestor.Api.Helper
{
    public static class SafeMessageHelper
    {
        /// <summary>
        /// Sanitizes a string for safe use in logs or user-facing messages by removing control characters like CR, LF, and tabs.
        /// </summary>
        /// <param name="input">The potentially unsafe string.</param>
        /// <returns>A sanitized version of the input string.</returns>
        public static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // Remove common control characters (CR, LF, TAB, etc.)
            var unsafeChars = new[] { '\r', '\n', '\t', '\f', '\v' };
            var sanitized = new string(input.Where(c => !unsafeChars.Contains(c)).ToArray());

            return sanitized.Trim(); // Trim leading/trailing whitespace
        }

        /// <summary>
        /// Sanitizes any object by converting to string and applying sanitization.
        /// </summary>
        /// <param name="input">Any object to sanitize.</param>
        /// <returns>Sanitized string.</returns>
        public static string Sanitize(object input)
        {
            return Sanitize(input?.ToString() ?? string.Empty);
        }
    }
}
