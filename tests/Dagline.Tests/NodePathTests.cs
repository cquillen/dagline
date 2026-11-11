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
    public void Every_character_a_segment_may_use_is_accepted_alone_and_inside_a_path()
    {
        foreach (var character in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-")
        {
            NodePath.Parse($"{character}").Segments.ShouldBe([$"{character}"]);
            NodePath.TryParse($"shard-3/a{character}b/{character}", out var path).ShouldBeTrue();
            path.Segments.ShouldBe(["shard-3", $"a{character}b", $"{character}"]);
        }
    }

    [Test]
    public void Every_other_character_is_rejected_wherever_it_is_in_a_segment()
    {
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-";
        var otherAscii = Enumerable.Range(0, 128).Select(code => (char)code).Where(character => character != '/' && !allowed.Contains(character));
        var beyondAscii = ((int[])[0x00E9, 0x0416, 0x0663, 0xFF21, 0x00A0, 0x2028]).Select(code => (char)code);

        foreach (var character in otherAscii.Concat(beyondAscii))
        {
            foreach (var text in (string[])[$"{character}", $"{character}load", $"load{character}", $"lo{character}ad", $"shard-3/lo{character}ad/verify"])
            {
                NodePath.TryParse(text, out var path).ShouldBeFalse($"U+{(int)character:X4} in '{text}'");
                path.ShouldBe(default);
                Should.Throw<FormatException>(() => NodePath.Parse(text));
            }
        }
    }

    [Test]
    [Arguments("/")]
    [Arguments("//")]
    [Arguments("shard-3/tenant-a/")]
    [Arguments("/shard-3/tenant-a")]
    [Arguments("shard-3/ /prepare")]
    [Arguments(" ")]
    [Arguments(" prepare")]
    [Arguments("prepare\n")]
    [Arguments("shard-3\\prepare")]
    public void A_path_with_an_empty_segment_or_white_space_is_rejected(string text)
    {
        NodePath.TryParse(text, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => NodePath.Parse(text));
    }

    [Test]
    public void Parent_walks_up_to_the_root_one_segment_at_a_time()
    {
        var path = NodePath.Parse("shard-3/tenant-a/prepare");
        var tenant = path.Parent.ShouldNotBeNull();
        var shard = tenant.Parent.ShouldNotBeNull();

        tenant.ShouldBe(NodePath.Parse("shard-3/tenant-a"));
        shard.ShouldBe(NodePath.Parse("shard-3"));
        shard.Parent.ShouldBeNull();
        default(NodePath).Parent.ShouldBeNull();
    }

    [Test]
    public void The_path_of_a_handle_at_the_root_has_no_parent_and_one_in_a_group_has_the_path_of_the_group()
    {
        var builder = new GraphBuilder();
        var shard = builder.Group("shard-3");
        var prepare = shard.Group("tenant-a").Task("prepare", _ => Task.CompletedTask);

        shard.Path.Parent.ShouldBeNull();
        builder.Task("publish", _ => Task.CompletedTask).Path.Parent.ShouldBeNull();
        prepare.Path.Parent.ShouldBe(NodePath.Parse("shard-3/tenant-a"));
        prepare.Path.ShouldBe(NodePath.Parse("shard-3/tenant-a/prepare"));
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
