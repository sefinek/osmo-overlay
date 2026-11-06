using Telemavix.Core.Localization;

namespace Telemavix.Tests.Localization;

[TestClass]
public static class TestCulture
{
	// Core's messages follow the UI culture - English here, whatever the machine running the tests.
	[AssemblyInitialize]
	public static void UseEnglish(TestContext _)
	{
		UiLanguages.Apply(UiLanguages.English);
	}
}
