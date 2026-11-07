using System.Globalization;

namespace OllamaDesktop.Core;

/// <summary>A bounded decimal parser, not a scripting engine. No variables, IO or reflection.</summary>
public static class Arithmetic
{
    public static decimal Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 2048) throw Invalid();
        try { return new Parser(expression).Read(); }
        catch (AppException) { throw; }
        catch (Exception e) when (e is OverflowException or DivideByZeroException or FormatException)
        { throw new AppException("CALC_RANGE", "Арифметическая ошибка: деление на ноль, неверное число или переполнение.", e); }
    }
    private static AppException Invalid() => new("CALC_INVALID", "Допустимы числа, + − * /, проценты, скобки и SUM/AVERAGE/MIN/MAX/ROUND/ABS.");
    private sealed class Parser(string source)
    {
        private int _position, _depth;
        public decimal Read() { var value = Sum(); Space(); if (_position != source.Length) throw Invalid(); return value; }
        private void Space() { while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++; }
        private bool Eat(char ch) { Space(); if (_position < source.Length && source[_position] == ch) { _position++; return true; } return false; }
        private decimal Sum()
        {
            var value = Product();
            while (true) { if (Eat('+')) value += Product(); else if (Eat('-')) value -= Product(); else return value; }
        }
        private decimal Product()
        {
            var value = Atom();
            while (true) { if (Eat('*')) value *= Atom(); else if (Eat('/')) value /= Atom(); else return value; }
        }
        private decimal Atom()
        {
            if (++_depth > 32) throw Invalid();
            try
            {
                decimal value;
                if (Eat('+')) value = Atom();
                else if (Eat('-')) value = -Atom();
                else if (Eat('(')) { value = Sum(); if (!Eat(')')) throw Invalid(); }
                else
                {
                    Space(); var start = _position;
                    while (_position < source.Length && char.IsAsciiLetter(source[_position])) _position++;
                    if (_position > start)
                    {
                        var name = source[start.._position].ToUpperInvariant(); if (!Eat('(')) throw Invalid();
                        var arguments = new List<decimal>();
                        do { arguments.Add(Sum()); if (arguments.Count > 256) throw Invalid(); } while (Eat(','));
                        if (!Eat(')')) throw Invalid();
                        value = name switch
                        {
                            "SUM" => arguments.Sum(), "AVERAGE" => arguments.Average(), "MIN" => arguments.Min(), "MAX" => arguments.Max(),
                            "ABS" when arguments.Count == 1 => Math.Abs(arguments[0]),
                            "ROUND" when arguments.Count == 2 && arguments[1] >= 0 && arguments[1] <= 12 && arguments[1] == decimal.Truncate(arguments[1])
                                => Math.Round(arguments[0], (int)arguments[1], MidpointRounding.AwayFromZero),
                            _ => throw Invalid()
                        };
                    }
                    else
                    {
                        while (_position < source.Length && (char.IsAsciiDigit(source[_position]) || source[_position] == '.')) _position++;
                        if (_position == start) throw Invalid();
                        value = decimal.Parse(source[start.._position], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    }
                }
                while (Eat('%')) value /= 100m;
                return value;
            }
            finally { _depth--; }
        }
    }
}
