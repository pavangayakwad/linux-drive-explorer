namespace FileExplorer.Api.Services;

/// <summary>
/// Decides whether a file's content is text (and can therefore be previewed as text) or a compiled
/// binary, archive, or media payload.
///
/// Names and extensions settle the cheap, unambiguous cases. Everything else is settled by sniffing the
/// first bytes of the file, because extension-less text files are the norm on Linux - "caddy" (a Caddyfile),
/// "Dockerfile", "authorized_keys", ".env.example", "README", a systemd unit, a shell script with no
/// suffix at all - and none of them can be recognised from their name. Conversely a file called "caddy"
/// sitting next to a config might be the actual Caddy executable, and the bytes are the only thing that
/// can tell the two apart. Compiled/media formats are recognised by their magic numbers, which catches
/// binaries that arrive with a misleading or missing extension.
/// </summary>
public static class FileContentInspector
{
    /// <summary>How much of the file is inspected when its name isn't decisive. Enough to recognise a
    /// magic number, a BOM, or UTF-16's tell-tale NUL padding, while staying trivially cheap to read even
    /// from a slow or remote mount - the answer never depends on the rest of a huge file.</summary>
    private const int SniffWindowBytes = 8192;

    private static readonly HashSet<string> KnownTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".json", ".jsonc", ".json5", ".ndjson", ".xml", ".csv", ".tsv",
        ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".env", ".editorconfig",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css", ".scss", ".sass", ".less", ".html", ".htm",
        ".cshtml", ".razor", ".vb", ".cs", ".fs", ".fsx", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".m", ".mm", ".php", ".pl", ".pm", ".lua", ".r", ".jl",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".vbs", ".sql", ".plsql",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".npmignore", ".mailmap",
        ".service", ".socket", ".timer", ".mount", ".path", ".target", ".desktop", ".link", ".mobileconfig",
        ".plist", ".manifest", ".config", ".csproj", ".vbproj", ".fsproj", ".sln", ".props", ".targets",
        ".rdm", ".reg", ".inf", ".nfo", ".srt", ".vtt", ".diff", ".patch", ".mbox", ".eml",
    };

    /// <summary>Extensions that are always worth refusing without even opening the file: executables,
    /// object code, packages, disk images, and fonts. Keeps a ".exe" or ".dll" from being previewed no
    /// matter what its first bytes happen to look like.</summary>
    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".sys", ".drv", ".ko", ".o", ".a", ".obj", ".lib", ".pdb", ".ilk",
        ".bin", ".class", ".jar", ".war", ".ear", ".apk", ".aab", ".msi", ".msp", ".appx", ".msix", ".app",
        ".deb", ".rpm", ".pkg", ".dmg", ".iso", ".img", ".vhd", ".vhdx", ".qcow2", ".vmdk", ".wim", ".esd",
        ".cab", ".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".zst", ".lz", ".lz4", ".tar", ".tgz", ".tzst",
        ".whl", ".nupkg", ".vsix", ".crx", ".xpi", ".pyc", ".pyo", ".pyd", ".wasm", ".ttf", ".otf",
        ".woff", ".woff2", ".eot", ".ttc", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods",
        ".odp", ".rtf", ".pdf", ".ps", ".eps", ".prn", ".scr", ".cpl", ".com", ".msu", ".ac3",
    };

    /// <summary>Files that are text but carry no usable extension, so content sniffing would be the only
    /// way in. Listed explicitly because it makes the common ones certain rather than probabilistic.</summary>
    private static readonly HashSet<string> KnownTextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "containerfile", "makefile", "gnumakefile", "caddyfile", "jenkinsfile", "vagrantfile",
        "gemfile", "rakefile", "procfile", "guardfile", "brewfile", "podfile", "fastfile", "cheffile",
        "readme", "license", "licence", "copying", "notice", "changelog", "authors", "contributors",
        "news", "todo", "install", "copying.left", "cmakelists", "cmakecache", "pkgbuild", "install.log",
        "authorized_keys", "known_hosts", "ssh_config", "sshd_config", "id_dsa", "id_ecdsa", "id_ed25519",
        "crontab", "fstab", "mtab", "crypttab", "hostname", "hosts", "hosts.allow", "hosts.deny", "issue",
        "motd", "shells", "passwd", "group", "shadow", "gshadow", "subuid", "subgid", "os-release",
        "bashrc", "bash_profile", "bash_logout", "bash_history", "zshrc", "zprofile", "zshenv", "profile",
        "tcshrc", "cshrc", "gitconfig", "gitignore", "gitattributes", "editorconfig", "npmrc", "nvmrc",
        "yarnrc", "htaccess", "htpasswd", "user.ini", "pylintrc", "flake8", "coverage", "condarc",
        "resolv.conf", "sources.list", "environment", "inputrc", "vimrc", "gvimrc", "exrc", "screenrc",
        "tmux.conf", "Xresources", "Xdefaults", "xinitrc", "xsession", "xsessionrc", "wallpaper",
    };

    /// <summary>Magic numbers of binary formats, checked against the head of the file. Anything found here
    /// is treated as binary even if its later bytes happen to be sparse enough to pass the text heuristic.</summary>
    private static readonly (int Offset, byte[] Signature)[] BinarySignatures =
    [
        (0, "MZ"u8.ToArray()),                             // PE / DOS (Windows executables, .NET single-file apps)
        (0, [0x7F, 0x45, 0x4C, 0x46]),                     // ELF (Linux executables and objects)
        (0, [0xCA, 0xFE, 0xBA, 0xBE]),                     // Java class / Mach-O fat binary
        (0, [0xFE, 0xED, 0xFE, 0xFA]),                     // Mach-O 64-bit
        (0, [0xCF, 0xFA, 0xED, 0xFE]),                     // Mach-O 64-bit, reversed
        (0, [0xCE, 0xFA, 0xED, 0xFE]),                     // Mach-O 32-bit
        (0, [0xFF, 0xFE, 0xED, 0xFE]),                     // Mach-O 32-bit, reversed
        (0, [0x00, 0x61, 0x73, 0x6D]),                     // WebAssembly module
        (0, "dex\n"u8.ToArray()),                          // Android DEX
        (0, [0x50, 0x4B, 0x03, 0x04]),                     // ZIP (and everything built on it: docx, jar, apk)
        (0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]),         // RAR
        (0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),         // 7-Zip
        (0, [0x1F, 0x8B]),                                 // gzip
        (0, [0x42, 0x5A, 0x68]),                           // bzip2
        (0, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),         // xz
        (0, [0x28, 0xB5, 0x2F, 0xFD]),                     // Zstandard
        (0, [0x25, 0x50, 0x44, 0x46]),                     // PDF
        (0, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]), // compound file (legacy Office)
        (0, [0x89, 0x50, 0x4E, 0x47]),                     // PNG
        (0, [0xFF, 0xD8, 0xFF]),                           // JPEG
        (0, [0x47, 0x49, 0x46, 0x38]),                     // GIF
        (0, [0x42, 0x4D]),                                 // BMP
        (0, [0x49, 0x49, 0x2A, 0x00]),                     // TIFF (little-endian)
        (0, [0x4D, 0x4D, 0x00, 0x2A]),                     // TIFF (big-endian)
        (0, [0x52, 0x49, 0x46, 0x46]),                     // RIFF (WAV / AVI)
        (0, [0x1A, 0x45, 0xDF, 0xA3]),                     // Matroska / WebM
        (0, [0x4F, 0x67, 0x67, 0x53]),                     // Ogg
        (0, [0x66, 0x4C, 0x61, 0x43]),                     // FLAC
        (0, [0x49, 0x44, 0x33]),                           // MP3 (ID3 tag)
        (0, [0xFF, 0xFB]),                                 // MP3 (MPEG audio frame)
        (0, [0xFF, 0xF3]),                                 // MP3 (MPEG audio frame)
        (0, [0xFF, 0xF1]),                                 // MP3 (MPEG audio frame)
        (0, [0x00, 0x00, 0x01, 0x00]),                     // ICO
        (0, [0x00, 0x00, 0x02, 0x00]),                     // CUR
        (0, "SQLite format 3"u8.ToArray()),                // SQLite database
        (0, "!<arch>"u8.ToArray()),                        // ar archive (and .deb)
        (0, [0xED, 0xAB, 0xEE, 0xDB]),                     // RPM
        (0, "OTTO"u8.ToArray()),                           // OpenType font
        (0, [0x00, 0x01, 0x00, 0x00]),                     // TrueType font
        (257, "ustar"u8.ToArray()),                        // POSIX tar
    ];

    public static bool HasKnownTextExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && KnownTextExtensions.Contains(extension);

    public static bool HasKnownBinaryExtension(string extension) =>
        !string.IsNullOrEmpty(extension) && KnownBinaryExtensions.Contains(extension);

    public static bool HasKnownTextFileName(string fileName) =>
        !string.IsNullOrEmpty(fileName)
        && (KnownTextFileNames.Contains(fileName) || fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase));

    /// <summary>Decides what the preview endpoint should do with a file whose name says nothing useful:
    /// opens just the head of it and judges the bytes.</summary>
    public static async Task<bool> LooksLikeTextAsync(string physicalPath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, SniffWindowBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var head = new byte[SniffWindowBytes];
            var read = await stream.ReadAsync(head.AsMemory(0, head.Length), ct);
            return LooksLikeText(head.AsSpan(0, read));
        }
        catch (IOException)
        {
            // Can't read it (deleted mid-request, permission lost, dead mount) - "not text" is the safe answer.
            return false;
        }
    }

    /// <summary>The actual classifier, over the first few kilobytes of a file.</summary>
    internal static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        // A file with nothing in it is a perfectly ordinary empty text file.
        if (head.IsEmpty)
        {
            return true;
        }

        // A BOM is a positive declaration of a text encoding; take it at its word.
        if (head.StartsWith(Utf8Bom) || head.StartsWith(Utf16LeBom) || head.StartsWith(Utf16BeBom))
        {
            return true;
        }

        foreach (var (offset, signature) in BinarySignatures)
        {
            if (head.Length >= offset + signature.Length && head.Slice(offset, signature.Length).SequenceEqual(signature))
            {
                return false;
            }
        }

        // NUL bytes never occur in a text file of any encoding this app is likely to meet (UTF-16 without a
        // BOM included - that is exactly what this catches). A single one is enough to call it binary, which
        // is how sparse data files, compiled objects, and core dumps are filtered out.
        if (head.Contains((byte)0x00))
        {
            return false;
        }

        // Anything that decodes cleanly as UTF-8 is text, including files full of high-bit bytes - accented
        // languages, Devanagari, box-drawing, emoji - which a plain "high bytes = binary" rule would reject.
        if (IsValidUtf8(head))
        {
            return true;
        }

        // Not valid UTF-8, so it is either a single-byte legacy encoding (Windows-1252, Latin-1, Shift-JIS...)
        // or raw binary. Text in those encodings still reads as printable characters plus the usual
        // whitespace, whereas binary payloads are dense with control bytes. Anything over 1% odd control
        // characters in the sample is treated as binary.
        var suspicious = 0;
        foreach (var b in head)
        {
            var isOrdinaryWhitespace = b is (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)0x08 or (byte)0x0B or (byte)0x0C;
            if (b < 0x20 && !isOrdinaryWhitespace || b == 0x7F)
            {
                suspicious++;
            }
        }

        return suspicious * 100 <= head.Length;
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
