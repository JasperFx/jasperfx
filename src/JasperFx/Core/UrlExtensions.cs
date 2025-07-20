namespace JasperFx.Core
{
    public static class UrlExtensions
    {
        public static bool Matches(this Uri subject, Uri match)
        {
            if (subject.Scheme != match.Scheme) return false;

            if (match.Host.IsEmpty()) return true;

            if (subject.Host != match.Host) return false;

            var subjectSegments = subject.Segments.Select(x => x.ToString().Trim('/')).Where(x => x.IsNotEmpty()).ToArray();
            var matchSegments = match.Segments.Select(x => x.ToString().Trim('/')).Where(x => x.IsNotEmpty()).ToArray();

            if (matchSegments.Length > subjectSegments.Length) return false;

            for (int i = 0; i < matchSegments.Length; i++)
            {
                var s = subjectSegments[i];
                var m = matchSegments[i];
                
                if (m == "*") continue;

                if (s != m) return false;
            }

            return true;
        }
        
        /// <summary>
        /// Smart helper to append two url strings together.  Takes care of the
        /// "/" joining for you.  
        /// </summary>
        /// <param name="url"></param>
        /// <param name="part"></param>
        /// <returns></returns>
        public static string AppendUrl(this string url, string part)
        {
            var composite = (url ?? string.Empty).TrimEnd('/') + "/" + part.TrimStart('/');

            return composite.TrimEnd('/');
        }

        /// <summary>
        /// Removes the first segment of a Url string
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public static string ChildUrl(this string url)
        {
            return url.Split('/').Skip(1).Join("/");
        }

        /// <summary>
        /// Removes the last segment of a Url string
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public static string ParentUrl(this string url)
        {
            url = url.Trim('/');
            return url.Contains("/") ? url.Split('/').Reverse().Skip(1).Reverse().Join("/") : string.Empty;
        }

        /// <summary>
        /// Slightly smarter version of ChildUrl() that handles
        /// empty url's
        /// </summary>
        /// <param name="relativeUrl"></param>
        /// <returns></returns>
        public static string MoveUp(this string relativeUrl)
        {
            if (relativeUrl.IsEmpty()) return relativeUrl;

            var segments = relativeUrl.Split('/');
            if (segments.Count() == 1) return string.Empty;

            return segments.Skip(1).Join("/");
        }

        /// <summary>
        /// Either returns the original Uri if the schema already matches or
        /// modifies the scheme of the Uri
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="scheme"></param>
        /// <returns></returns>
        public static Uri MaybeCorrectScheme(this Uri uri, string scheme)
        {
            if (uri.Scheme == scheme) return uri;
            return new Uri($"{scheme}://{uri.Authority}{uri.PathAndQuery.TrimEnd('/')}");
        }
    }
}