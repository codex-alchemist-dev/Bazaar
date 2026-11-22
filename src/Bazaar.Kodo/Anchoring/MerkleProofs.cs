namespace Bazaar.Kodo.Anchoring;

/// <summary>Proof that the leaf at <see cref="LeafIndex"/> is in a tree of <see cref="TreeSize"/> leaves.</summary>
public sealed record MerkleInclusionProof(long LeafIndex, long TreeSize, IReadOnlyList<byte[]> Path);

/// <summary>Proof that the tree of <see cref="FirstSize"/> leaves is a prefix of the tree of <see cref="SecondSize"/> leaves.</summary>
public sealed record MerkleConsistencyProof(long FirstSize, long SecondSize, IReadOnlyList<byte[]> Path);
