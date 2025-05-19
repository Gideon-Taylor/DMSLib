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
        NEW, UPDATE, SAME, NONE, MISSING, COLUMNS_CHANGED
    }

    public class CompareResult
    {
        public DMSCompareStatus Status;
        public List<int> ChangedIndexes = new List<int>();
        public List<int> AddedIndexes = new List<int>();
        public List<int> DeletedIndexes = new List<int>();
    }
}