namespace Dagline.Tests;

public class NodePathTests
{
    [Test]
    public void Parse_reads_the_segments_of_a_path()
    {
        NodePath.Parse("shard-3/tenant-a/prepare").Segments.ShouldBe(["shard-3", "tenant-a", "prepare"]);
        NodePath.Parse("prepare").Segments.ShouldBe(["prepare"]);
    }

    [Test]
    public void A_segment_may_use_letters_digits_dot_underscore_and_hyphen()
    {
        NodePath.Parse("AZ.az_09-/v1.2").Segments.ShouldBe(["AZ.az_09-", "v1.2"]);
    }

    [Test]
    public void ToString_prints_the_segments_joined_by_a_slash()
    {
        NodePath.Parse("shard-3/tenant-a/prepare").ToString().ShouldBe("shard-3/tenant-a/prepare");
    }

    [Test]
    public void Parent_is_the_path_without_its_last_segment_and_null_at_the_root()
    {
        NodePath.Parse("shard-3/tenant-a/prepare").Parent.ShouldBe(NodePath.Parse("shard-3/tenant-a"));
        NodePath.Parse("prepare").Parent.ShouldBeNull();
    }

    [Test]
    public void Paths_with_the_same_text_are_equal_and_case_matters()
    {
        var path = NodePath.Parse("shard-3/prepare");

        (path == NodePath.Parse("shard-3/prepare")).ShouldBeTrue();
        path.GetHashCode().ShouldBe(NodePath.Parse("shard-3/prepare").GetHashCode());
        (path == NodePath.Parse("shard-3/Prepare")).ShouldBeFalse();
        (path != NodePath.Parse("shard-3")).ShouldBeTrue();
    }

    [Test]
    public void TryParse_gives_the_same_path_as_Parse()
    {
        NodePath.TryParse("shard-3/prepare", out var path).ShouldBeTrue();

        path.ShouldBe(NodePath.Parse("shard-3/prepare"));
    }

    [Test]
    [Arguments("")]
    [Arguments("/prepare")]
    [Arguments("prepare/")]
    [Arguments("shard-3//prepare")]
    [Arguments("shard 3/prepare")]
    public void Text_that_is_not_a_path_is_rejected(string text)
    {
        Should.Throw<FormatException>(() => NodePath.Parse(text)).Message.ShouldContain($"'{text}'");
        NodePath.TryParse(text, out _).ShouldBeFalse();
    }

    [Test]
    public void Null_is_not_a_path()
    {
        Should.Throw<ArgumentNullException>(() => NodePath.Parse(null!));
        NodePath.TryParse(null, out _).ShouldBeFalse();
    }

    [Test]
    public void The_path_of_a_task_at_the_root_is_its_segment()
    {
        var task = new GraphBuilder().Task("prepare", _ => Task.CompletedTask);

        task.Path.ShouldBe(NodePath.Parse("prepare"));
    }
}
