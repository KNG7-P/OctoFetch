using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OctoFetch.Helpers
{
    public static class TagInferrer
    {
        private static readonly Dictionary<string, string> ExtensionToTag =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // -- Movies / video ------------------------------------------
                ["mp4"] = "Movies", ["mkv"] = "Movies", ["avi"] = "Movies",
                ["mov"] = "Movies", ["wmv"] = "Movies", ["flv"] = "Movies",
                ["webm"] = "Movies", ["m4v"] = "Movies", ["mpg"] = "Movies",
                ["mpeg"] = "Movies", ["ts"] = "Movies", ["m2ts"] = "Movies",
                ["mts"] = "Movies", ["vob"] = "Movies", ["3gp"] = "Movies",
                ["3g2"] = "Movies", ["ogv"] = "Movies", ["rmvb"] = "Movies",
                ["rm"] = "Movies", ["divx"] = "Movies", ["xvid"] = "Movies",
                ["asf"] = "Movies", ["mxf"] = "Movies", ["f4v"] = "Movies",

                // -- Music / audio -------------------------------------------
                ["mp3"] = "Music", ["flac"] = "Music", ["wav"] = "Music",
                ["ogg"] = "Music", ["oga"] = "Music", ["m4a"] = "Music",
                ["aac"] = "Music", ["opus"] = "Music", ["alac"] = "Music",
                ["ape"] = "Music", ["wma"] = "Music", ["aiff"] = "Music",
                ["aif"] = "Music", ["dsf"] = "Music", ["dff"] = "Music",
                ["mid"] = "Music", ["midi"] = "Music", ["amr"] = "Music",
                ["ac3"] = "Music", ["dts"] = "Music",

                // -- Software (installers, packages, OS images) --------------
                ["exe"] = "Software", ["msi"] = "Software", ["msix"] = "Software",
                ["msixbundle"] = "Software", ["appx"] = "Software",
                ["appxbundle"] = "Software", ["msu"] = "Software",
                ["deb"] = "Software", ["rpm"] = "Software", ["snap"] = "Software",
                ["dmg"] = "Software", ["pkg"] = "Software", ["app"] = "Software",
                ["apk"] = "Software", ["xapk"] = "Software", ["aab"] = "Software",
                ["ipa"] = "Software", ["jar"] = "Software", ["war"] = "Software",
                ["run"] = "Software", ["bin"] = "Software",
                ["appimage"] = "Software", ["flatpak"] = "Software",
                ["iso"] = "Software", ["img"] = "Software", ["vmdk"] = "Software",
                ["vhd"] = "Software", ["vhdx"] = "Software", ["ova"] = "Software",

                // -- Games (cartridge dumps, console images) -----------------
                ["nsp"] = "Games", ["xci"] = "Games", ["nes"] = "Games",
                ["smc"] = "Games", ["sfc"] = "Games", ["gb"] = "Games",
                ["gbc"] = "Games", ["gba"] = "Games", ["nds"] = "Games",
                ["3ds"] = "Games", ["cia"] = "Games", ["n64"] = "Games",
                ["z64"] = "Games", ["v64"] = "Games", ["gen"] = "Games",
                ["smd"] = "Games", ["32x"] = "Games", ["wbfs"] = "Games",
                ["gcm"] = "Games", ["wud"] = "Games", ["wux"] = "Games",
                ["rom"] = "Games", ["chd"] = "Games", ["cso"] = "Games",
                ["pbp"] = "Games",

                // -- Books / comics ------------------------------------------
                ["pdf"] = "Books", ["epub"] = "Books", ["mobi"] = "Books",
                ["azw"] = "Books", ["azw3"] = "Books", ["fb2"] = "Books",
                ["lit"] = "Books", ["prc"] = "Books", ["djvu"] = "Books",
                ["cbr"] = "Books", ["cbz"] = "Books", ["cbt"] = "Books",
                ["cb7"] = "Books",

                // -- Documents / office --------------------------------------
                ["doc"] = "Documents", ["docx"] = "Documents",
                ["xls"] = "Documents", ["xlsx"] = "Documents",
                ["xlsm"] = "Documents", ["ppt"] = "Documents",
                ["pptx"] = "Documents", ["odt"] = "Documents",
                ["ods"] = "Documents", ["odp"] = "Documents",
                ["txt"] = "Documents", ["rtf"] = "Documents",
                ["csv"] = "Documents", ["tsv"] = "Documents",
                ["md"] = "Documents", ["html"] = "Documents",
                ["htm"] = "Documents", ["xml"] = "Documents",
                ["json"] = "Documents", ["yaml"] = "Documents",
                ["yml"] = "Documents", ["tex"] = "Documents",
                ["log"] = "Documents",
            };

        public static string? InferFromUrlOrName(string? urlOrName)
        {
            if (string.IsNullOrWhiteSpace(urlOrName)) return null;

            var cleaned = urlOrName.Split('?', '#')[0];

            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
                cleaned = uri.AbsolutePath;

            var name = cleaned.Split('/', '\\').LastOrDefault();
            if (string.IsNullOrWhiteSpace(name)) return null;

            var ext = Path.GetExtension(name).TrimStart('.');
            if (!string.IsNullOrEmpty(ext) &&
                ExtensionToTag.TryGetValue(ext, out var tag))
                return tag;

            var withoutOuter = Path.GetFileNameWithoutExtension(name);
            var inner = Path.GetExtension(withoutOuter).TrimStart('.');
            if (!string.IsNullOrEmpty(inner) &&
                ExtensionToTag.TryGetValue(inner, out var innerTag))
                return innerTag;

            return null;
        }

        public static string Resolve(string? userTag, string? urlOrName)
        {
            if (!string.IsNullOrWhiteSpace(userTag) &&
                !string.Equals(userTag, "Other", StringComparison.OrdinalIgnoreCase))
                return userTag;

            return InferFromUrlOrName(urlOrName) ?? "Other";
        }
    }
}
