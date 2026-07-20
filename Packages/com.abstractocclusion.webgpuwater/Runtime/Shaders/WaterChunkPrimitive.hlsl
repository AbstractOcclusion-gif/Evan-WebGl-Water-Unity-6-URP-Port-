// WebGpuWater - water CHUNK primitive intersection (box + sphere), in POOL space.
// ONE switch point for every chunk consumer (reuse, never rewrite): the cylinder / wedge and the
// baked-SDF (arbitrary-mesh) primitives slot in here. Everything works in the body's POOL space -
// the unit shape spanning [-1, 1] per axis (xz in [-1,1] is the footprint; the volume frame's
// PoolToWorld / WorldToPool place and size it), so rotation and non-uniform extent are the frame's.
#ifndef WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED
#define WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED

#include "WaterShared.hlsl" // IntersectCube + RAY_SLAB_EPSILON

// C# pair: WaterVolume.ChunkFootprint (published as _ChunkShape). Values are the enum ordinals - 1
// (None isn't drawn), so Box = 0, Sphere = 1.
#define CHUNK_SHAPE_BOX              0.0
#define CHUNK_SHAPE_SPHERE           1.0
#define CHUNK_SHAPE_SPHERE_THRESHOLD 0.5

// Half-extent of the unit shape in POOL space: the [-1, 1] cube, and the INSCRIBED sphere's radius.
#define CHUNK_POOL_HALF_EXTENT 1.0

// Ray vs the inscribed unit sphere (centre 0, radius CHUNK_POOL_HALF_EXTENT) in POOL space. The
// direction is passed UNNORMALISED (poolDir = WorldDirToPool(worldDir)): because pool space is an
// affine image of world space, the parameter t of a NORMALISED world ray is preserved, so tNear/tFar
// come out in WORLD metres - the same convention IntersectCube uses. Returns an EMPTY interval
// (tNear > tFar) on a miss so callers get a zero-length column.
float2 IntersectUnitSphere(float3 origin, float3 dir)
{
    float a = dot(dir, dir);
    float b = dot(origin, dir);
    float c = dot(origin, origin) - CHUNK_POOL_HALF_EXTENT * CHUNK_POOL_HALF_EXTENT;
    float discriminant = b * b - a * c;
    if (discriminant < 0.0) return float2(1.0, -1.0); // ray misses the sphere
    float root = sqrt(discriminant);
    float invA = 1.0 / max(a, RAY_SLAB_EPSILON);
    return float2((-b - root) * invA, (-b + root) * invA);
}

// (tNear, tFar) of the selected chunk primitive along a POOL-space ray, in world metres.
float2 ChunkIntersect(float shape, float3 origin, float3 dir)
{
    if (shape >= CHUNK_SHAPE_SPHERE_THRESHOLD)
        return IntersectUnitSphere(origin, dir);
    return IntersectCube(origin, dir,
                         float3(-CHUNK_POOL_HALF_EXTENT, -CHUNK_POOL_HALF_EXTENT, -CHUNK_POOL_HALF_EXTENT),
                         float3( CHUNK_POOL_HALF_EXTENT,  CHUNK_POOL_HALF_EXTENT,  CHUNK_POOL_HALF_EXTENT));
}

// Outward POOL-space surface normal at a point ON the primitive's surface (the ray's entry point),
// for the sun facet + refraction. Sphere: the radial direction. Box: the dominant-axis face. Map to
// world with PoolNormalToWorld (inverse-transpose of the frame) at the call site.
float3 ChunkSurfaceNormalPool(float shape, float3 surfacePoint)
{
    if (shape >= CHUNK_SHAPE_SPHERE_THRESHOLD)
        return normalize(surfacePoint);
    float3 a = abs(surfacePoint);
    if (a.x >= a.y && a.x >= a.z) return float3(sign(surfacePoint.x), 0.0, 0.0);
    if (a.y >= a.z)               return float3(0.0, sign(surfacePoint.y), 0.0);
    return float3(0.0, 0.0, sign(surfacePoint.z));
}

#endif // WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED
