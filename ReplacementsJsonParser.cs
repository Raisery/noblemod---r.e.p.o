using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NobleMod;

/// <summary>
/// Parse minimal pour <c>replacements.json</c> : valeurs soit <c>"fichier.ogg"</c>, soit un tableau
/// <c>[{ "file": "...", "weight": 70 }, { "vanilla": true, "weight": 30 }, ...]</c> (poids en %, normalises si somme != 100).
/// </summary>
internal static class ReplacementsJsonParser
{
    internal sealed class Entry
    {
        public readonly string MappingKey;
        public readonly List<Variant> Variants = new List<Variant>();

        public Entry(string mappingKey) => MappingKey = mappingKey;
    }

    internal struct Variant
    {
        public string File;
        public float WeightPercent;
        /// <summary>Si vrai : jouer le clip vanilla d'origine pour ce tirage (pas de <c>file</c> requis).</summary>
        public bool IsVanilla;
    }

    internal static List<Entry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<Entry>();

        var p = new Parser(json.Trim());
        return p.ParseRootObject();
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s;
            _i = 0;
        }

        public List<Entry> ParseRootObject()
        {
            SkipWs();
            Expect('{');
            var list = new List<Entry>();
            SkipWs();
            while (!Eof && Peek() != '}')
            {
                var key = ReadString();
                SkipWs();
                Expect(':');
                SkipWs();
                var entry = new Entry(key);
                if (Peek() == '[')
                    ReadVariantArray(entry.Variants);
                else if (Peek() == '"')
                    entry.Variants.Add(new Variant { File = ReadString(), WeightPercent = 100f, IsVanilla = false });
                else
                    throw new FormatException($"Valeur attendue pour la cle '{key}' : chaine ou tableau.");

                list.Add(entry);
                SkipWs();
                if (Peek() == ',')
                {
                    _i++;
                    SkipWs();
                }
                else if (Peek() != '}')
                    throw new FormatException("',' ou '}' attendu apres une entree.");
            }

            Expect('}');
            SkipWs();
            if (!Eof)
                throw new FormatException("Caracteres inattendus apres la racine JSON.");
            return list;
        }

        private void ReadVariantArray(List<Variant> target)
        {
            Expect('[');
            SkipWs();
            while (!Eof && Peek() != ']')
            {
                Expect('{');
                SkipWs();
                string file = null;
                float? weight = null;
                bool? vanilla = null;
                while (!Eof && Peek() != '}')
                {
                    var prop = ReadString();
                    SkipWs();
                    Expect(':');
                    SkipWs();
                    if (string.Equals(prop, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Peek() != '"')
                            throw new FormatException("\"file\" doit etre une chaine.");
                        file = ReadString();
                    }
                    else if (string.Equals(prop, "weight", StringComparison.OrdinalIgnoreCase))
                        weight = ReadNumber();
                    else if (string.Equals(prop, "vanilla", StringComparison.OrdinalIgnoreCase))
                        vanilla = ReadJsonBool();
                    else
                        SkipValue();

                    SkipWs();
                    if (Peek() == ',')
                    {
                        _i++;
                        SkipWs();
                    }
                    else if (Peek() != '}')
                        throw new FormatException("Propriete d'objet : ',' ou '}' attendu.");
                }

                Expect('}');
                var isVanilla = vanilla == true;
                if (!isVanilla && string.IsNullOrWhiteSpace(file))
                    throw new FormatException("Variante sans \"file\" (ou utilisez \"vanilla\": true).");
                if (weight == null)
                    throw new FormatException(isVanilla ? "Variante vanilla sans \"weight\"." : $"Variante sans \"weight\" pour '{file}'.");
                target.Add(new Variant
                {
                    File = isVanilla ? null : file.Trim(),
                    WeightPercent = weight.Value,
                    IsVanilla = isVanilla
                });

                SkipWs();
                if (Peek() == ',')
                {
                    _i++;
                    SkipWs();
                }
                else if (Peek() != ']')
                    throw new FormatException("',' ou ']' attendu dans le tableau de variantes.");
            }

            Expect(']');
        }

        private void SkipValue()
        {
            if (Peek() == '"')
            {
                ReadString();
                return;
            }

            if (Peek() == '[')
            {
                var depth = 0;
                while (!Eof)
                {
                    var c = _s[_i++];
                    if (c == '[') depth++;
                    else if (c == ']')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }

                return;
            }

            if (Peek() == '{')
            {
                var depth = 0;
                while (!Eof)
                {
                    var c = _s[_i++];
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }

                return;
            }

            SkipWs();
            if (!Eof && _i + 4 <= _s.Length && string.CompareOrdinal(_s, _i, "true", 0, 4) == 0)
            {
                _i += 4;
                return;
            }

            if (!Eof && _i + 5 <= _s.Length && string.CompareOrdinal(_s, _i, "false", 0, 5) == 0)
            {
                _i += 5;
                return;
            }

            if (!Eof && _i + 4 <= _s.Length && string.CompareOrdinal(_s, _i, "null", 0, 4) == 0)
            {
                _i += 4;
                return;
            }

            while (!Eof && !char.IsWhiteSpace(_s[_i]) && _s[_i] != ',' && _s[_i] != '}' && _s[_i] != ']')
                _i++;
        }

        private bool ReadJsonBool()
        {
            SkipWs();
            if (_i + 4 <= _s.Length && string.CompareOrdinal(_s, _i, "true", 0, 4) == 0)
            {
                _i += 4;
                return true;
            }

            if (_i + 5 <= _s.Length && string.CompareOrdinal(_s, _i, "false", 0, 5) == 0)
            {
                _i += 5;
                return false;
            }

            throw new FormatException("Litteral JSON true ou false attendu.");
        }

        private float ReadNumber()
        {
            SkipWs();
            var start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+'))
                _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i]))
                _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i]))
                    _i++;
            }

            var span = _s.Substring(start, _i - start);
            if (!float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Nombre invalide : '{span}'");
            return v;
        }

        private string ReadString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (!Eof && _s[_i] != '"')
            {
                if (_s[_i] == '\\')
                {
                    _i++;
                    if (Eof)
                        throw new FormatException("Chaine non terminee (echappement).");
                    var c = _s[_i++];
                    switch (c)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(c); break;
                    }
                }
                else
                    sb.Append(_s[_i++]);
            }

            Expect('"');
            return sb.ToString();
        }

        private void SkipWs()
        {
            while (!Eof && char.IsWhiteSpace(_s[_i]))
                _i++;
        }

        private char Peek() => Eof ? '\0' : _s[_i];

        private bool Eof => _i >= _s.Length;

        private void Expect(char c)
        {
            if (Eof || _s[_i] != c)
                throw new FormatException($"Caractere '{c}' attendu, position {_i}.");
            _i++;
        }
    }
}
