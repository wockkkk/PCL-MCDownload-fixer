using System;
using System.Collections.Generic;

namespace PCLauncher.Scan
{
    public enum SiteKind
    {
        Unknown = 0,
        StubThrow,
        InlineThrow,
        Hint,
        CompareTrap
    }

    public sealed class MarkerSite
    {
        public int Token;
        public string DeclaringType;
        public string MethodName;
        public string Signature;
        public SiteKind Kind;
        public int IlSize;
        public string Window;
        public string Action;

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case SiteKind.StubThrow: return "stub-throw";
                    case SiteKind.InlineThrow: return "inline-throw";
                    case SiteKind.Hint: return "hint";
                    case SiteKind.CompareTrap: return "runtime-compare(trap)";
                    default: return "unknown";
                }
            }
        }
    }

    public sealed class UpdateTarget
    {
        public int Token;
        public string DeclaringType;
        public string MethodName;
        public string Signature;
    }

    public sealed class LoaderSite
    {
        public bool Present;
        public int Token;
        public string DeclaringType;
        public string MethodName;
        public string DummyTitle;
        public string DummyCtor;
        public string LoaderField;
        public bool PatternMatched;
        public List<string> Newobjs = new List<string>();
        public List<string> Calls = new List<string>();
    }

    public sealed class ScanReport
    {
        public string FilePath;
        public long FileSize;
        public string AssemblyName;
        public string AssemblyVersion;
        public string RuntimeVersion;
        public string Architecture;
        public string EntryPoint;
        public string Sha256;
        public int TypeCount;
        public int MethodCount;
        public int FieldCount;
        public string RulesSource = "";
        public List<MarkerSite> Sites = new List<MarkerSite>();
        public List<UpdateTarget> UpdateTargets = new List<UpdateTarget>();
        public LoaderSite Loader = new LoaderSite();
        public List<string> Notes = new List<string>();
        public List<string> Warnings = new List<string>();
        public DateTime ScannedAt = DateTime.Now;
    }
}
