using Mpai.Mmc.Cts;

namespace Mpai.Aif.Tests;

// THE LANGUAGE OF A COLUMN (MMC-CTS-V2.5), found from its text, chooses the voice that speaks it. A sentence or two of each
// language the voices speak, one of each language the voices do not yet speak (Japanese, Chinese), and what is too short or
// too mixed to place.
[Trait("Group", "Fast")]
public class LanguageDetectorTests
{
    [Theory]
    [InlineData("en", "Here is what NPR needs, in the order I would do it. None of it needs to start before the General Assembly, and the decisions are to be taken with you first.")]
    [InlineData("fr", "Le suffrage universel n'aime pas les programmes compliqués; il préfère les formules simples qui répondent nettement aux exigences de la situation, dans le pays et dans les villes.")]
    [InlineData("de", "Der Kaiser hat heute die Abgeordneten des Landes empfangen und mit ihnen über die Lage der Stadt gesprochen, die sich nach dem Krieg noch nicht erholt hat.")]
    [InlineData("es", "El periódico de la ciudad publicó ayer que los vecinos de la calle han pedido al alcalde una solución para el problema, pero no han recibido respuesta todavía.")]
    [InlineData("pt", "O jornal da cidade publicou ontem que os moradores da rua pediram ao prefeito uma solução para o problema, mas não receberam ainda nenhuma resposta.")]
    [InlineData("it", "Il giornale della città ha pubblicato ieri che gli abitanti della strada hanno chiesto al sindaco una soluzione per il problema, ma non hanno ancora ricevuto una risposta.")]
    [InlineData("zh", "今天上海的街上有很多人在看報紙，他們說這件事情是昨天發生的。")]
    [InlineData("ja", "今日は東京の新聞に大きな記事が出ていて、みんながそれを読んでいます。")]
    public void TheLanguageIsFound(string expected, string text) => Assert.Equal(expected, LanguageDetector.Detect(text));

    [Theory]
    [InlineData("")]
    [InlineData("Pour ou contre")]
    [InlineData("Do reolt anss les e")]
    public void TooLittleOrTooUncertainGivesNothing(string text) => Assert.Null(LanguageDetector.Detect(text));
}
