using System.Text.RegularExpressions;

namespace AChatWeb.Services;

public static class RegistrationRules
{
    // Mirrors TransferProtocol.checkPasswordForSafe(). The server remains authoritative.
    public static string? Validate(string login, string password, string confirmation)
    {
        if (string.IsNullOrWhiteSpace(login)) return "Введите логин.";
        if (login.Length > 100) return "Логин должен быть не длиннее 100 символов.";
        if (login.Contains('$')) return "В логине нельзя использовать символ $.";
        if (password.Length < 8) return "В пароле должно быть не меньше 8 символов.";
        if (password.Length > 256) return "Пароль должен быть не длиннее 256 символов.";
        if (!password.Any(c => c is >= 'a' and <= 'z') ||
            !password.Any(c => c is >= 'A' and <= 'Z') ||
            !password.Any(c => c is >= '0' and <= '9'))
            return "Добавьте строчную и заглавную латинские буквы и цифру.";
        if (password.Contains("1234") || password.Contains("qwerty") || password.Contains("password"))
            return "Не используйте последовательности 1234, qwerty или password.";
        if (Regex.IsMatch(password, @"([A-Za-z0-9_])\1{3,}") || password.Contains('\n') || password.Contains('\r'))
            return "Не повторяйте один символ четыре раза подряд и не используйте переносы строк.";
        if (password != confirmation) return "Пароли не совпадают.";
        return null;
    }
}
