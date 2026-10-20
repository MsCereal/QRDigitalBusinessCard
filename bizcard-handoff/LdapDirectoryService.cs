using System.Text;
yestic string EscapeFilterValue(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                '/' => "\\2f",
                _ => c.ToString()
            });
        }

        return builder.ToString();
    }
}
