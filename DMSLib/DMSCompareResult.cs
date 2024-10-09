using DMSLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DMSLib
{
    public enum DMSCompareStatus
    {
        NEW, UPDATE, SAME, NONE, MISSING
    }

    public struct CompareResult
    {
        public DMSCompareStatus Status;
        public List<int> ChangedIndexes;
    }
}