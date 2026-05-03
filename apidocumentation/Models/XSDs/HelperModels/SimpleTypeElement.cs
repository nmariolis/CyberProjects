using System.Collections.Generic;

namespace Avra.Travel.Domain.Documentation.HelperModels
{
    public class SimpleTypeElement 
    {
        public MinMaxClass MinMaxType { get; private set; }
        public PatternClass PatternType { get; private set; }
        public EnumClass EnumType { get; private set; }

        public SimpleTypeElement() { }

        public SimpleTypeElement (MinMaxClass minMaxType)
        {
            MinMaxType = minMaxType;
        }

        public SimpleTypeElement(PatternClass patternType)
        {
            PatternType = patternType;
        }

        public SimpleTypeElement(EnumClass enumType)
        {
            EnumType = enumType;
        }
    }

    public class MinMaxClass 
    {
        public string MinValue { get; private set; }
        public string MaxValue { get; private set; }

        public MinMaxClass(string minValue, string maxValue)
        {
            MinValue = minValue;
            MaxValue = maxValue;
        }
    }

    public class PatternClass
    {
        public string Pattern { get; private set; }

        public PatternClass(string pattern)
        {
            Pattern = pattern;
        }
    }

    public class EnumClass
    {
        public List<string> Enums { get; set; }
        public string DefaultEnum { get; set; }

        public EnumClass(List<string> enums)
        {
            Enums = new List<string>();
            Enums = enums;
        }
    }
}
