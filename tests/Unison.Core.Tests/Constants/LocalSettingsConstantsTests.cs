using System.Linq;
using System.Reflection;
using Unison.Core.Constants;
using Xunit;

namespace Unison.Core.Tests.Constants
{
    public class LocalSettingsConstantsTests
    {
        [Fact]
        public void Every_setting_key_has_a_default()
        {
            var missing = typeof(LocalSettingsConstants)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue())
                .Where(key => !LocalSettingsConstants.Defaults.ContainsKey(key))
                .ToList();

            Assert.Empty(missing);
        }
    }
}
