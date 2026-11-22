namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// The steps of the end-to-end path that cannot run yet. Each is a skipped test that names the gap in GAPS.md, so the list is
/// in the test report and a step turns into a real test the day its feature lands. A skip here is not a pass.
/// </summary>
public sealed class GapTests
{
    [Fact(Skip = "GAP 1: no first-run install. The host starts only from hand-written configuration; no setup wizard run, bundled Postgres or service registration is exercised (GAPS.md).")]
    public void A_clean_install_starts_with_bundled_postgres() { }

    [Fact(Skip = "GAP 2: no way to create a company, a group chat or an agent at run time. The harness writes them into the schema (GAPS.md).")]
    public void The_owner_creates_a_company_with_an_agent() { }

    [Fact(Skip = "GAP 3: sign-in is the dev bearer token. Pairing, token signing and refresh (#797, #807) are not exercised (GAPS.md).")]
    public void The_owner_pairs_a_device_and_signs_in_with_a_signed_token() { }

    [Fact(Skip = "GAP 4: the client side is the wire client only. No Bazaar.Client.* app code runs headless against the server (GAPS.md).")]
    public void The_headless_client_shell_signs_in_and_shows_the_chat() { }

    [Fact(Skip = "GAP 5: a task through the Kanban board and the turn loop is not exercised; AcceptanceTests covers chat to commit with harness stand-ins (GAPS.md, gaps 9 to 11).")]
    public void An_agent_picks_up_a_board_card_and_finishes_it() { }
}
