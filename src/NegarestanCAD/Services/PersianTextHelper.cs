using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NegarestanCAD.Services;

/// <summary>
/// ابزارهای متن فارسی، تاریخ شمسی، تبدیل ارقام، و پاک‌سازی/اصلاح متون فارسی DWG (TTF و SHX قدیمی)
/// </summary>
public static class PersianTextHelper
{
    private static readonly PersianCalendar ShamsiCalendar = new();

    static PersianTextHelper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string GetTodayShamsi(bool persianDigits = false)
    {
        var now = DateTime.Now;
        int y = ShamsiCalendar.GetYear(now);
        int m = ShamsiCalendar.GetMonth(now);
        int d = ShamsiCalendar.GetDayOfMonth(now);
        string date = $"{y:0000}/{m:00}/{d:00}";
        return persianDigits ? ToPersianDigits(date) : date;
    }

    public static string FormatNumber(double value, int decimals, bool persianDigits)
    {
        string formatted = value.ToString($"N{decimals}", CultureInfo.InvariantCulture);
        return persianDigits ? ToPersianDigits(formatted) : formatted;
    }

    public static string FormatPlainNumber(double value, int decimals, bool persianDigits)
    {
        string formatted = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        return persianDigits ? ToPersianDigits(formatted) : formatted;
    }

    public static string ToPersianDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(c is >= '0' and <= '9' ? (char)('\u06F0' + (c - '0')) : c);
        }
        return sb.ToString();
    }

    public static string ToEnglishDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c is >= '\u06F0' and <= '\u06F9')
                sb.Append((char)('0' + (c - '\u06F0')));
            else if (c is >= '\u0660' and <= '\u0669')
                sb.Append((char)('0' + (c - '\u0660')));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// پاک‌سازی کدهای فرمت AutoCAD (MText/Text)، تبدیل \U+XXXX و اصلاح کدپیج و متون فارسی قدیمی
    /// </summary>
    public static string CleanCadText(string? raw, bool fixLegacyPersianShx = true)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        string text = raw;

        // 1. دیکد کاراکترهای \U+XXXX (متداول در فایل‌های DWG/DXF قدیمی‌تر برای متون فارسی)
        text = Regex.Replace(text, @"\\[Uu]\+([0-9A-Fa-f]{4})", m =>
        {
            int code = Convert.ToInt32(m.Groups[1].Value, 16);
            return ((char)code).ToString();
        });

        // 2. تبدیل نمادهای استاندارد اتوکد
        text = text.Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
                   .Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
                   .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase)
                   .Replace("%%u", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("%%o", "", StringComparison.OrdinalIgnoreCase);

        // 3. حذف کدهای فرمت‌بندی MText اتوکد
        text = text.Replace(@"\P", "\n")
                   .Replace(@"\~", " ");

        // حذف تگ‌های کنترلی مانند \fTahoma|b0|i0|c178|p34; و \H2.5; و \C1; و \A1;
        text = Regex.Replace(text, @"\\[fFhHcCwWqQtTaAoOlLpP][^;\\}]*;", string.Empty);
        text = Regex.Replace(text, @"\\[LlOoKk]", string.Empty);
        text = text.Replace("{", string.Empty).Replace("}", string.Empty);

        // 4. بازیابی کدپیج Windows-1256 و تبدیل متون فونت‌های قدیمی SHX فارسی (Farsi.shx / Khat.shx)
        if (fixLegacyPersianShx)
        {
            text = TryFixWindows1256Mojibake(text);
            text = TryDecodeLegacyShxPersian(text);
            text = NormalizeArabicPresentationForms(text);
        }

        return text.Trim();
    }


    private static string TryDecodeLegacyShxPersian(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // اگر از قبل یونیکد فارسی دارد یا صرفاً عدد/مقیاس/انگلیسی استاندارد است دست نزن
        if (input.Any(c => c is >= '\u0600' and <= '\u06FF')) return input;
        if (Regex.IsMatch(input, @"^[\d\s/\.\-:,\+\-\(\)%°Ø±]+$")) return input;
        if (Regex.IsMatch(input, @"^[A-Z0-9\s_\-\./:]+$")) return input; // مثل NORTH یا 1/1000

        // تشخیص الگوی متون SHX فارسی (وجود کاراکترهای خاص یا ترکیبات کیبورد فارسی مثل nvd–، u£–¢، ©، ±، ¿)
        const string shxMarkers = "–—£¢©±¿æ»â<>œ";
        bool hasShxMarker = input.Any(c => shxMarkers.Contains(c)) ||
                            (input.Contains('-') && input.Any(char.IsLower));
        if (!hasShxMarker) return input;

        var map = new Dictionary<char, char>
        {
            // حروف استاندارد کیبورد فارسی در فونت‌های Farsi.shx / Khat.shx / Iran.shx
            ['a'] = 'ش',
            ['b'] = 'ذ',
            ['c'] = 'ز',
            ['d'] = 'ی',
            ['e'] = 'ث',
            ['f'] = 'ب',
            ['g'] = 'ل',
            ['h'] = 'ا',
            ['i'] = 'ه',
            ['j'] = 'ت',
            ['k'] = 'ن',
            ['l'] = 'م',
            ['m'] = 'ئ',
            ['n'] = 'د',
            ['o'] = 'خ',
            ['p'] = 'ح',
            ['q'] = 'ض',
            ['r'] = 'ق',
            ['s'] = 'س',
            ['t'] = 'ف',
            ['u'] = 'ع',
            ['v'] = 'ر',
            ['w'] = 'ص',
            ['x'] = 'ط',
            ['y'] = 'غ',
            ['z'] = 'ظ',
            ['['] = 'ج',
            [']'] = 'چ',
            [';'] = 'ک',
            ['\''] = 'گ',
            [','] = 'و',
            ['.'] = 'ن',
            ['>'] = 'ه',
            ['<'] = 'و',

            // کاراکترهای فرم چسبان/پایانی در Khat.shx / Farsi.shx
            ['–'] = 'ا',
            ['—'] = 'ا',
            ['£'] = 'م',
            ['¢'] = 'ن',
            ['©'] = 'د',
            ['±'] = 'ر',
            ['¿'] = 'ی',
            ['æ'] = 'ه',
            ['»'] = 'ه',
            ['â'] = 'و',
            ['K'] = 'م',
            ['E'] = 'ی',
            ['I'] = 'م',
            ['G'] = 'ل',
            ['H'] = 'ا',
            ['D'] = 'د',
            ['S'] = 'س',
            ['R'] = 'ر',
            ['N'] = 'ن',
            ['T'] = 'ت',
            ['B'] = 'ب',
            ['P'] = 'پ',
            ['C'] = 'چ',
            ['V'] = 'ر',
            ['L'] = 'م',

            ['-'] = 'ا',
            ['œ'] = 'خ',
            ['~'] = 'ی',
            ['`'] = 'پ',
        };

        var sb = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            if (map.TryGetValue(ch, out char fa))
                sb.Append(fa);
            else
                sb.Append(ch);
        }

        return sb.ToString();
    }


    private static string TryFixWindows1256Mojibake(string input)
    {
        // اگر متن شامل حروف فارسی/عربی یونیکد نیست اما کاراکترهای محدوده 0xC0..0xFF دارد
        bool hasPersian = input.Any(c => (c >= '\u0600' && c <= '\u06FF') || (c >= '\uFB50' && c <= '\uFEFF'));
        if (hasPersian) return input;

        int highLatinCount = input.Count(c => c >= 0xC0 && c <= 0xFF);
        if (highLatinCount < 2) return input;

        try
        {
            byte[] latinBytes = Encoding.GetEncoding(1252).GetBytes(input);
            string decoded1256 = Encoding.GetEncoding(1256).GetString(latinBytes);
            int persianAfter = decoded1256.Count(c => c >= '\u0600' && c <= '\u06FF');
            if (persianAfter >= highLatinCount / 2)
                return decoded1256;
        }
        catch
        {
            // اگر کدپیج در دسترس نبود همان متن اصلی برگردد
        }

        return input;
    }

    /// <summary>
    /// نرمال‌سازی فرم‌های نمایشی (Presentation Forms) و حروف عربی/فارسی که در فونت‌های قدیمی مثل Farsi.shx و Khat.shx ثبت می‌شوند
    /// </summary>
    private static string NormalizeArabicPresentationForms(string input)
    {
        // استانداردسازی ک و ی عربی به فارسی
        string normalized = input.Normalize(NormalizationForm.FormKC)
            .Replace('\u0643', '\u06A9') // ك -> ک
            .Replace('\u064A', '\u06CC'); // ي -> ی

        return normalized;
    }
}
