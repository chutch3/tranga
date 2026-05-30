using System.Text;

namespace API;

public static class Utils
{
    //https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file
    //less than 32 is control *forbidden*
    //34 is " *forbidden*
    //42 is * *forbidden*
    //47 is / *forbidden*
    //58 is : *forbidden*
    //60 is < *forbidden*
    //62 is > *forbidden*
    //63 is ? *forbidden*
    //92 is \ *forbidden*
    //124 is | *forbidden*
    //127 is delete *forbidden*
    //Below 127 all except *******
    private static readonly int[] ForbiddenCharsBelow127 = [34, 42, 47, 58, 60, 62, 63, 92, 124, 127];
    //Above 127 none except *******
    private static readonly int[] IncludeCharsAbove127 = [128, 138, 142];
    //128 is € include
    //138 is Š include
    //142 is Ž include
    //152 through 255 looks fine except 157, 172, 173, 175 *******
    private static readonly int[] ForbiddenCharsAbove152 = [157, 172, 173, 175];
    public static string CleanNameForWindows(this string name)
    {
        StringBuilder sb = new ();
        foreach (char c in name)
        {
            if (c >= 32 && c < 127 && !ForbiddenCharsBelow127.Contains(c))
                sb.Append(c);
            else if (c > 127 && c < 152 && IncludeCharsAbove127.Contains(c))
                sb.Append(c);
            else if(c >= 152 && c <= 255 && !ForbiddenCharsAbove152.Contains(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns a uniformly random element of <paramref name="list"/>. Every element, including the last,
    /// is reachable (the upper bound is exclusive, so it must be the count — not count-1).
    /// </summary>
    public static T RandomElement<T>(this IReadOnlyList<T> list, Random? random = null)
    {
        if (list.Count == 0)
            throw new ArgumentException("List must not be empty", nameof(list));
        return list[(random ?? Random.Shared).Next(0, list.Count)];
    }

    /// <summary>
    /// Builds a URI
    /// </summary>
    public static Uri BuildUri(string basePath, string relativePath) => BuildUri(new Uri(basePath), relativePath);
    
    public static Uri BuildUri(Uri basePath, string relativePath) => new (new Uri(basePath.AbsoluteUri.TrimEnd('/') + '/'), relativePath.TrimStart('/')); //Shenanigans
}