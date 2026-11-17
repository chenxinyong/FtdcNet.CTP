/////////////////////////////////////////////////////////////////////////
//// 上期技术 Ftdc C++ => .Net Framework Adapter
//// Author : shawn666.liu@hotmail.com
/////////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;

namespace CTP
{
    /// <summary>
    /// 郑商所（CZCE）合约代码工具。
    ///
    /// CTP 老品种使用 3 位年月（SA709），部分研究/存储侧希望统一为 4 位（SA2709）。
    /// 本类只做“展示/落盘”归一化；订阅、下单等 CTP 调用必须继续使用前置返回的原始代码。
    /// </summary>
    public static class CzceInstrumentCode
    {
        /// <summary>
        /// 把 CZCE 3 位年月合约代码归一化为 4 位；非 CZCE 或 4 位代码原样返回。
        /// </summary>
        /// <param name="exchangeId">交易所代码，仅 CZCE 生效。</param>
        /// <param name="instrumentId">前置返回的 InstrumentID，例如 SA709。</param>
        /// <param name="referenceDate">参考日期，用于推断年份（一般传当前交易日）。</param>
        /// <param name="expireDate">合约到期日（CtP ExpireDate，yyyyMMdd），优先用它确定年份。</param>
        public static string Normalize(
            string exchangeId,
            string instrumentId,
            DateTime referenceDate,
            string expireDate = null)
        {
            if (!string.Equals(exchangeId, "CZCE", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(instrumentId))
                return instrumentId;

            var i = 0;
            while (i < instrumentId.Length && char.IsLetter(instrumentId[i]))
                i++;
            if (i == 0 || i == instrumentId.Length)
                return instrumentId;

            var suffix = instrumentId.Substring(i);
            if (suffix.Length != 3)
                return instrumentId;
            if (!int.TryParse(suffix.Substring(1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var month) ||
                month < 1 || month > 12)
                return instrumentId;

            var yearDigit = suffix[0] - '0';
            var year = ResolveYear(expireDate, yearDigit, month, referenceDate);
            if (year <= 0)
                return instrumentId;

            return instrumentId.Substring(0, i).ToUpperInvariant() +
                   (year % 100).ToString("D2", CultureInfo.InvariantCulture) +
                   month.ToString("D2", CultureInfo.InvariantCulture);
        }

        private static int ResolveYear(
            string expireDate,
            int yearDigit,
            int month,
            DateTime referenceDate)
        {
            if (!string.IsNullOrEmpty(expireDate) &&
                DateTime.TryParseExact(expireDate, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expire) &&
                expire.Month == month &&
                expire.Year % 10 == yearDigit)
                return expire.Year;

            // 回退规则：取不早于参考年份、末位数字匹配的年份。
            var year = referenceDate.Year;
            while (year % 10 != yearDigit)
                year++;
            return year;
        }
    }
}
