namespace FileExplorer.Api.Services;

public static class FileTypeLabeler
{
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "Text Document",
        [".pdf"] = "PDF File",
        [".doc"] = "Microsoft Word Document",
        [".docx"] = "Microsoft Word Document",
        [".xls"] = "Microsoft Excel Worksheet",
        [".xlsx"] = "Microsoft Excel Worksheet",
        [".ppt"] = "Microsoft PowerPoint Presentation",
        [".pptx"] = "Microsoft PowerPoint Presentation",
        [".jpg"] = "JPG File",
        [".jpeg"] = "JPEG File",
        [".png"] = "PNG File",
        [".gif"] = "GIF File",
        [".bmp"] = "Bitmap Image",
        [".svg"] = "SVG File",
        [".zip"] = "Compressed (zipped) Folder",
        [".rar"] = "RAR Archive",
        [".7z"] = "7-Zip Archive",
        [".tar"] = "Tape Archive",
        [".gz"] = "GZip Archive",
        [".mp3"] = "MP3 Audio",
        [".mp4"] = "MP4 Video",
        [".mkv"] = "MKV Video",
        [".mov"] = "QuickTime Video",
        [".sql"] = "SQL File",
        [".json"] = "JSON File",
        [".xml"] = "XML File",
        [".csv"] = "CSV File",
        [".md"] = "Markdown Document",
        [".html"] = "HTML Document",
        [".css"] = "CSS File",
        [".js"] = "JavaScript File",
        [".ts"] = "TypeScript File",
        [".cs"] = "C# Source File",
        [".sh"] = "Shell Script",
        [".log"] = "Log File",
    };

    public static string Resolve(bool isDirectory, string? extension)
    {
        if (isDirectory)
        {
            return "File folder";
        }

        if (string.IsNullOrEmpty(extension))
        {
            return "File";
        }

        if (KnownLabels.TryGetValue(extension, out var label))
        {
            return label;
        }

        var ext = extension.TrimStart('.').ToUpperInvariant();
        return $"{ext} File";
    }
}
