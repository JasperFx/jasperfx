using JasperFx.Pagination;
using Shouldly;

namespace CoreTests.Pagination;

public class PagedListTests
{
    private static PagedList<int> page(int count, long total, int pageNumber, int pageSize)
        => new(Enumerable.Range(1, count).ToArray(), total, pageNumber, pageSize);

    [Fact]
    public void the_first_page_of_several()
    {
        var list = page(10, 25, 1, 10);

        list.PageCount.ShouldBe(3);
        list.IsFirstPage.ShouldBeTrue();
        list.IsLastPage.ShouldBeFalse();
        list.HasPreviousPage.ShouldBeFalse();
        list.HasNextPage.ShouldBeTrue();
        list.FirstItemOnPage.ShouldBe(1);
        list.LastItemOnPage.ShouldBe(10);
    }

    [Fact]
    public void a_page_in_the_middle()
    {
        var list = page(10, 25, 2, 10);

        list.IsFirstPage.ShouldBeFalse();
        list.IsLastPage.ShouldBeFalse();
        list.HasPreviousPage.ShouldBeTrue();
        list.HasNextPage.ShouldBeTrue();
        list.FirstItemOnPage.ShouldBe(11);
        list.LastItemOnPage.ShouldBe(20);
    }

    [Fact]
    public void a_short_last_page_counts_only_what_it_holds()
    {
        var list = page(5, 25, 3, 10);

        list.IsLastPage.ShouldBeTrue();
        list.HasNextPage.ShouldBeFalse();
        list.FirstItemOnPage.ShouldBe(21);
        list.LastItemOnPage.ShouldBe(25);
    }

    [Fact]
    public void a_total_that_fills_its_last_page_exactly_makes_no_extra_page()
    {
        page(10, 20, 2, 10).PageCount.ShouldBe(2);
    }

    // Polecat reported item 41 of 25 as this page's first and item 25 as its last. A page past the end has no
    // items, and reporting one is worse than reporting none.
    [Fact]
    public void a_page_past_the_end_reports_no_items()
    {
        var list = page(0, 25, 5, 10);

        list.FirstItemOnPage.ShouldBe(0);
        list.LastItemOnPage.ShouldBe(0);
        list.IsLastPage.ShouldBeTrue();
        list.HasNextPage.ShouldBeFalse();
        list.TotalItemCount.ShouldBe(25);
    }

    [Fact]
    public void an_empty_result_has_no_pages()
    {
        var list = page(0, 0, 1, 10);

        list.PageCount.ShouldBe(0);
        list.Count.ShouldBe(0);
        list.FirstItemOnPage.ShouldBe(0);
        list.LastItemOnPage.ShouldBe(0);
        list.IsFirstPage.ShouldBeTrue();
        list.IsLastPage.ShouldBeTrue();
    }

    // Polecat divided by this unchecked.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void a_page_size_that_is_not_positive_has_no_pages(int pageSize)
    {
        page(0, 25, 1, pageSize).PageCount.ShouldBe(0);
    }

    [Fact]
    public void a_total_beyond_an_int_is_kept()
    {
        var list = page(10, 5_000_000_000L, 1, 1000);

        list.TotalItemCount.ShouldBe(5_000_000_000L);
        list.PageCount.ShouldBe(5_000_000);
    }

    [Fact]
    public void it_is_the_list_of_its_items()
    {
        IPagedList<int> list = new PagedList<int>([7, 8, 9], 3, 1, 10);

        list.Count.ShouldBe(3);
        list[1].ShouldBe(8);
        list.ToArray().ShouldBe([7, 8, 9]);
    }

    [Fact]
    public void it_reads_covariantly_as_the_stores_interfaces_did()
    {
        IPagedList<object> list = new PagedList<string>(["a"], 1, 1, 10);

        list[0].ShouldBe("a");
    }

    [Fact]
    public void null_items_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => new PagedList<int>(null!, 0, 1, 10));
    }
}
