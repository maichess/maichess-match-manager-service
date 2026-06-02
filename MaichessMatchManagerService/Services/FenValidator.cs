namespace MaichessMatchManagerService.Services;

// Structural validation of a FEN string used to seed a custom match start
// position. Checks the six standard fields, piece-placement geometry, and that
// exactly one king of each colour is present. Full move-legality is not assessed
// here — a well-formed but strategically illegal position is accepted, matching
// how an engine treats an arbitrary start FEN.
internal static class FenValidator
{
    internal static bool IsValid(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
        {
            return false;
        }

        string[] parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6
            && IsValidPlacement(parts[0])
            && IsValidActiveColor(parts[1])
            && IsValidCastling(parts[2])
            && IsValidEnPassant(parts[3])
            && IsNonNegativeInteger(parts[4])
            && IsPositiveInteger(parts[5]);
    }

    private static bool IsValidPlacement(string placement)
    {
        string[] ranks = placement.Split('/');
        if (ranks.Length != 8)
        {
            return false;
        }

        int whiteKings = 0;
        int blackKings = 0;

        foreach (string rank in ranks)
        {
            int squares = 0;
            bool previousWasDigit = false;

            foreach (char c in rank)
            {
                if (c is >= '1' and <= '8')
                {
                    if (previousWasDigit)
                    {
                        return false;
                    }

                    squares += c - '0';
                    previousWasDigit = true;
                    continue;
                }

                if (!"pnbrqkPNBRQK".Contains(c))
                {
                    return false;
                }

                if (c == 'K')
                {
                    whiteKings++;
                }
                else if (c == 'k')
                {
                    blackKings++;
                }

                squares++;
                previousWasDigit = false;
            }

            if (squares != 8)
            {
                return false;
            }
        }

        return whiteKings == 1 && blackKings == 1;
    }

    private static bool IsValidActiveColor(string field) => field is "w" or "b";

    private static bool IsValidCastling(string field)
    {
        if (field == "-")
        {
            return true;
        }

        if (field.Length > 4)
        {
            return false;
        }

        foreach (char c in field)
        {
            if (!"KQkq".Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidEnPassant(string field) =>
        field == "-" ||
        (field.Length == 2 && field[0] is >= 'a' and <= 'h' && field[1] is >= '1' and <= '8');

    private static bool IsNonNegativeInteger(string field) =>
        int.TryParse(field, out int value) && value >= 0;

    private static bool IsPositiveInteger(string field) =>
        int.TryParse(field, out int value) && value >= 1;
}
