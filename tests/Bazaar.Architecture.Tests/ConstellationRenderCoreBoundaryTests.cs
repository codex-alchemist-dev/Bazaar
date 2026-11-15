namespace Bazaar.Architecture.Tests;

/// <summary>
/// Boundary of the generic renderer core (Bazaar.Constellation.Rendering.Core), which is prepared for later
/// extraction. The design note is docs/design/constellation-render-core.md.
/// </summary>
public class ConstellationRenderCoreBoundaryTests
{
    private static readonly string _coreDirectory =
        Path.Combine(RepoPaths.SourceDirectory("Bazaar.Constellation.Rendering"), "Core");

    [Fact]
    public void Core_source_stays_inside_the_core_namespace()
    {
        var sources = ArchitectureRules.SourceFiles([_coreDirectory]).ToList();

        // An empty folder would pass the rule vacuously, so the folder must exist and hold source.
        Assert.NotEmpty(sources);
        Assert.Empty(ArchitectureRules.ConstellationCoreViolations(sources));
    }

    [Fact]
    public void Rendering_project_declares_no_project_references() =>
        Assert.Empty(ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile("Bazaar.Constellation.Rendering")));

    [Fact]
    public void Compiled_rendering_assembly_references_no_other_bazaar_assembly() =>
        Assert.Empty(ArchitectureRules.ConstellationCoreAssemblyViolations(
            ArchitectureRules.ReferencedNames(typeof(Bazaar.Constellation.Rendering.Core.Camera2D).Assembly)));

    [Fact]
    public void Seeded_core_using_of_contracts_is_flagged() =>
        Assert.Single(ArchitectureRules.ConstellationCoreViolations(
            [("src/Bazaar.Constellation.Rendering/Core/Seeded.cs", "using Bazaar.Contracts.Graph;\n")]));

    [Fact]
    public void Seeded_core_using_of_adapter_namespace_is_flagged() =>
        Assert.Single(ArchitectureRules.ConstellationCoreViolations(
            [("src/Bazaar.Constellation.Rendering/Core/Seeded.cs", "using Bazaar.Constellation.Rendering;\n")]));

    [Fact]
    public void Seeded_core_namespace_mismatch_is_flagged() =>
        Assert.Single(ArchitectureRules.ConstellationCoreViolations(
            [("src/Bazaar.Constellation.Rendering/Core/Seeded.cs", "namespace Bazaar.Other;\n")]));

    [Fact]
    public void Seeded_core_cref_to_adapter_is_flagged() =>
        Assert.Single(ArchitectureRules.ConstellationCoreViolations(
            [("src/Bazaar.Constellation.Rendering/Core/Seeded.cs", "/// <see cref=\"IConstellationRenderer.Attach\"/>\n")]));

    [Fact]
    public void Seeded_core_mention_of_adapter_type_is_flagged() =>
        Assert.Single(ArchitectureRules.ConstellationCoreViolations(
            [("src/Bazaar.Constellation.Rendering/Core/Seeded.cs", "var scene = typeof(Bazaar.Constellation.Rendering.ConstellationScene);\n")]));

    [Fact]
    public void Seeded_clean_core_source_is_not_flagged() =>
        Assert.Empty(ArchitectureRules.ConstellationCoreViolations(
            [(
                "src/Bazaar.Constellation.Rendering/Core/Clean.cs",
                """
                using System;
                using Bazaar.Constellation.Rendering.Core;

                namespace Bazaar.Constellation.Rendering.Core.Sub;

                public sealed class Clean { }
                """)]));

    [Fact]
    public void Seeded_contracts_assembly_in_core_is_flagged() =>
        Assert.Equal(["Bazaar.Contracts"], ArchitectureRules.ConstellationCoreAssemblyViolations(["System.Runtime", "Bazaar.Contracts"]));

    [Fact]
    public void Seeded_rendering_and_core_assembly_names_are_allowed() =>
        Assert.Empty(ArchitectureRules.ConstellationCoreAssemblyViolations(
            ["System.Runtime", "Bazaar.Constellation.Rendering", "Bazaar.Constellation.Rendering.Core"]));
}
