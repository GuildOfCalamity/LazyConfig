using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SampleApp;

public static class Extensions
{
    /// <summary>
    /// Scans all public instance string properties of the provided object
    /// and returns a list of names for those properties that are null or empty.
    /// </summary>
    /// <param name="obj">The class object to scan.</param>
    /// <param name="valueToApplyIfEmpty">The value to apply if the property is empty. If no value is supplied this step will be skipped.</param>
    /// <returns>A list of property names whose string values are null or empty.</returns>
    public static List<string> GetEmptyStringProperties(object obj, string valueToApplyIfEmpty = "")
    {
        List<string> emptyProperties = new List<string>();

        if (obj == null)
            return emptyProperties;

        // Get all public instance properties from the object's type.
        PropertyInfo[] properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo property in properties)
        {
            // We only care about properties of type string that have a getter.
            if (property.PropertyType == typeof(string) && property.CanRead)
            {
                string? value = property.GetValue(obj) as string;
                if (string.IsNullOrEmpty(value))
                {
                    if (!string.IsNullOrEmpty(valueToApplyIfEmpty))
                        property.SetValue(obj, valueToApplyIfEmpty);

                    emptyProperties.Add(property.Name);
                }
            }
        }

        return emptyProperties;
    }

    public static byte[] ToByteArray(this string hex)
    {
        try
        {
            return Enumerable.Range(0, hex.Length)
               .Where(x => x % 2 == 0)
               .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
               .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Sequentially combines multiple <see cref="byte"/> arrays into a single <see cref="byte"/> array.
    /// </summary>
    /// <param name="arrays">two or more <see cref="byte"/> arrays</param>
    /// <returns>combined <see cref="byte"/> array</returns>
    public static byte[] IntegrateArrays(params byte[][] arrays)
    {
        if (arrays == null)
            return Array.Empty<byte>();

        // Calculate total length treating null entries as empty arrays.
        int totalLength = 0;
        foreach (var array in arrays)
            totalLength += array?.Length ?? 0;

        byte[] result = new byte[totalLength];
        int offset = 0;

        // Copy each array sequentially into the result.
        foreach (var array in arrays)
        {
            if (array == null) { continue; }
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }

        return result;
    }

    /// <summary>
    /// Similar to <see cref="GetReadableTime(TimeSpan)"/>.
    /// </summary>
    /// <param name="timeSpan"><see cref="TimeSpan"/></param>
    /// <returns>formatted text</returns>
    public static string ToReadableString(this TimeSpan span)
    {
        var parts = new StringBuilder();
        if (span.Days > 0)
            parts.Append($"{span.Days} day{(span.Days == 1 ? string.Empty : "s")} ");
        if (span.Hours > 0)
            parts.Append($"{span.Hours} hour{(span.Hours == 1 ? string.Empty : "s")} ");
        if (span.Minutes > 0)
            parts.Append($"{span.Minutes} minute{(span.Minutes == 1 ? string.Empty : "s")} ");
        if (span.Seconds > 0)
            parts.Append($"{span.Seconds} second{(span.Seconds == 1 ? string.Empty : "s")} ");
        if (span.Milliseconds > 0)
            parts.Append($"{span.Milliseconds} millisecond{(span.Milliseconds == 1 ? string.Empty : "s")} ");

        if (parts.Length == 0) // result was less than 1 millisecond
            return $"{span.TotalMilliseconds:N4} milliseconds"; // similar to span.Ticks
        else
            return parts.ToString().Trim();
    }
}
