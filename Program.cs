using System.Globalization;
using System.Text;
using System.Text.Json;

internal sealed class AppConfig
{
    public string RootDirectory { get; set; } = ".";
    public string InputFile { get; set; } = "folders.txt";
    public string OutputFile { get; set; } = "result.csv";
    public string LogFile { get; set; } = "calculator.log";

    public List<string> DirectoriesToScan { get; set; } = new();

    public string SizeUnit { get; set; } = "MB";
    public string CsvDelimiter { get; set; } = ",";
    public int DecimalPlaces { get; set; } = 2;
}

internal sealed class FileLogger
{
    private readonly string _logPath;

    public FileLogger(string logPath)
    {
        _logPath = logPath;
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string entry =
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} " +
            $"[{level}] {message}";

        try
        {
            string? directory = Path.GetDirectoryName(_logPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(
                _logPath,
                entry + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Не удалось записать журнал: {ex.Message}");
        }
    }
}

internal sealed class SizeCalculator
{
    private readonly FileLogger _logger;

    public SizeCalculator(FileLogger logger)
    {
        _logger = logger;
    }

    public decimal GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.Info(
                $"Каталог не найден, размер принят за 0: {path}");

            return 0m;
        }

        decimal totalBytes = 0m;
        var directories = new Stack<string>();

        directories.Push(path);

        while (directories.Count > 0)
        {
            string currentDirectory = directories.Pop();

            // Обрабатываем файлы текущего каталога.
            try
            {
                foreach (string file in
                         Directory.EnumerateFiles(currentDirectory))
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is UnauthorizedAccessException)
                    {
                        _logger.Warn(
                            $"Не удалось прочитать файл '{file}': " +
                            ex.Message);
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                _logger.Warn(
                    $"Ошибка перечисления файлов в " +
                    $"'{currentDirectory}': {ex.Message}");
            }

            // Обрабатываем вложенные каталоги.
            try
            {
                foreach (string subdirectory in
                         Directory.EnumerateDirectories(currentDirectory))
                {
                    try
                    {
                        FileAttributes attributes =
                            File.GetAttributes(subdirectory);

                        // Не переходим по символьным ссылкам
                        // и другим точкам повторной обработки.
                        if ((attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            directories.Push(subdirectory);
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is UnauthorizedAccessException)
                    {
                        _logger.Warn(
                            $"Не удалось проверить каталог " +
                            $"'{subdirectory}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                _logger.Warn(
                    $"Ошибка перечисления каталогов в " +
                    $"'{currentDirectory}': {ex.Message}");
            }
        }

        return totalBytes;
    }
}

internal static class Program
{
    private static void Main()
    {
        string configPath = Path.Combine(
            AppContext.BaseDirectory,
            "appsettings.json");

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine(
                $"Не найден файл конфигурации: {configPath}");
            return;
        }

        AppConfig config;

        try
        {
            string json = File.ReadAllText(configPath);

            config = JsonSerializer.Deserialize<AppConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidDataException(
                    "Файл конфигурации пуст или содержит null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Ошибка чтения конфигурации: {ex.Message}");
            return;
        }

        string configDirectory =
            Path.GetDirectoryName(configPath)!;

        string rootPath = Path.GetFullPath(
            config.RootDirectory,
            configDirectory);

        string inputPath = ResolvePath(
            config.InputFile, rootPath);

        string outputPath = ResolvePath(
            config.OutputFile, rootPath);

        string logPath = ResolvePath(
            config.LogFile, rootPath);

        var logger = new FileLogger(logPath);

        try
        {
            ValidateConfig(config);

            logger.Info("Начало работы программы.");
            logger.Info($"Корневой каталог: {rootPath}");
            logger.Info($"Входной файл: {inputPath}");
            logger.Info($"Выходной файл: {outputPath}");

            if (!Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException(
                    $"Корневой каталог не найден: {rootPath}");
            }

            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException(
                    "Не найден входной файл.", inputPath);
            }

            List<string> folderNames = File.ReadAllLines(inputPath)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            // Проверяем пути директорий заранее.
            List<(string Name, string Path)> scanDirectories =
                config.DirectoriesToScan
                    .Select(name => (
                        Name: name,
                        Path: ResolveChildPath(rootPath, name)))
                    .ToList();

            var calculator = new SizeCalculator(logger);

            string? outputDirectory =
                Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            using var writer = new StreamWriter(
                outputPath,
                false,
                new UTF8Encoding(true));

            string delimiter = config.CsvDelimiter;

            var headers = new List<string> { "Folder" };

            headers.AddRange(
                scanDirectories.Select(
                    d => $"{d.Name}_{config.SizeUnit}"));

            headers.Add($"Total_{config.SizeUnit}");

            writer.WriteLine(string.Join(
                delimiter,
                headers.Select(EscapeCsv)));

            int processed = 0;

            foreach (string folderName in folderNames)
            {
                if (!IsValidFolderName(folderName))
                {
                    logger.Warn(
                        $"Пропущено некорректное имя папки: " +
                        $"{folderName}");
                    continue;
                }

                decimal[] sizesBytes =
                    new decimal[scanDirectories.Count];

                for (int i = 0; i < scanDirectories.Count; i++)
                {
                    string folderPath = ResolveChildPath(
                        scanDirectories[i].Path,
                        folderName);

                    sizesBytes[i] =
                        calculator.GetDirectorySize(folderPath);
                }

                // Сначала суммируем байты, затем конвертируем.
                decimal totalBytes = sizesBytes.Sum();

                decimal divisor = GetUnitDivisor(config.SizeUnit);

                decimal[] sizes = sizesBytes
                    .Select(bytes => bytes / divisor)
                    .ToArray();

                decimal totalSize = totalBytes / divisor;

                var row = new List<string> { folderName };

                row.AddRange(sizes.Select(
                    size => FormatSize(
                        size, config.DecimalPlaces)));

                row.Add(FormatSize(
                    totalSize, config.DecimalPlaces));

                writer.WriteLine(string.Join(
                    delimiter,
                    row.Select(EscapeCsv)));

                writer.Flush();

                processed++;

                logger.Info(
                    $"Обработана папка '{folderName}'. " +
                    $"Итог: {FormatSize(totalSize, config.DecimalPlaces)} " +
                    $"{config.SizeUnit}.");

                Console.WriteLine(
                    $"{folderName}: " +
                    $"{FormatSize(totalSize, config.DecimalPlaces)} " +
                    $"{config.SizeUnit}");
            }

            logger.Info(
                $"Обработка завершена. Успешно обработано: {processed}.");

            logger.Info($"CSV сохранён: {outputPath}");

            Console.WriteLine();
            Console.WriteLine($"CSV: {outputPath}");
            Console.WriteLine($"Лог: {logPath}");
        }
        catch (Exception ex)
        {
            logger.Error($"Критическая ошибка: {ex}");

            Console.Error.WriteLine(
                $"Ошибка: {ex.Message}. Подробности: {logPath}");
        }
    }

    private static string ResolvePath(
        string path,
        string baseDirectory)
    {
        return Path.GetFullPath(path, baseDirectory);
    }

    private static string ResolveChildPath(
        string parent,
        string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"Ожидался относительный путь: {relativePath}");
        }

        string fullParent = Path.GetFullPath(parent);
        string fullPath = Path.GetFullPath(
            Path.Combine(fullParent, relativePath));

        string prefix = Path.TrimEndingDirectorySeparator(fullParent)
            + Path.DirectorySeparatorChar;

        StringComparison comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (!fullPath.StartsWith(prefix, comparison))
        {
            throw new InvalidDataException(
                $"Путь выходит за пределы родительского каталога: " +
                $"{relativePath}");
        }

        return fullPath;
    }

    private static bool IsValidFolderName(string name)
    {
        return name != "." &&
               name != ".." &&
               !Path.IsPathRooted(name) &&
               !name.Contains('/') &&
               !name.Contains('\\') &&
               name.IndexOfAny(
                   Path.GetInvalidFileNameChars()) < 0;
    }

    private static decimal GetUnitDivisor(string unit)
    {
        return unit.ToUpperInvariant() switch
        {
            "B" => 1m,
            "KB" => 1024m,
            "MB" => 1024m * 1024m,
            "GB" => 1024m * 1024m * 1024m,
            "TB" => 1024m * 1024m * 1024m * 1024m,
            _ => throw new InvalidDataException(
                $"Неподдерживаемая единица измерения: {unit}")
        };
    }

    private static string FormatSize(
        decimal value,
        int decimalPlaces)
    {
        return value.ToString(
            "F" + decimalPlaces,
            CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void ValidateConfig(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RootDirectory))
            throw new InvalidDataException(
                "RootDirectory не задан.");

        if (string.IsNullOrWhiteSpace(config.InputFile) ||
            string.IsNullOrWhiteSpace(config.OutputFile) ||
            string.IsNullOrWhiteSpace(config.LogFile))
        {
            throw new InvalidDataException(
                "InputFile, OutputFile и LogFile обязательны.");
        }

        if (config.DirectoriesToScan.Count == 0)
        {
            throw new InvalidDataException(
                "DirectoriesToScan не должен быть пустым.");
        }

        if (config.DirectoriesToScan.Any(
                string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                "В DirectoriesToScan обнаружен пустой путь.");
        }

        if (config.DecimalPlaces < 0 ||
            config.DecimalPlaces > 10)
        {
            throw new InvalidDataException(
                "DecimalPlaces должен быть от 0 до 10.");
        }

        if (config.CsvDelimiter.Length != 1 ||
            config.CsvDelimiter is "\"" or "\r" or "\n")
        {
            throw new InvalidDataException(
                "CsvDelimiter должен быть одним символом.");
        }

        // Проверяем допустимость единицы измерения.
        GetUnitDivisor(config.SizeUnit);
    }
}
