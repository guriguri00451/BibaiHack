using System;
using NUnit.Framework;
using UnityEngine;

namespace Seeker.Ar.Tests
{
    public class MapDefinitionTests
    {
        private const string FullJson =
            "{\"mapId\":\"bibai-museum\",\"name\":\"美唄\",\"modelUrl\":\"mock/a.glb\"," +
            "\"transform\":{\"position\":[1,2,3],\"rotation\":[0,0.6,0,0.8],\"scale\":[2,2,2]}}";

        [Test]
        public void Parse_ReadsModelUrl()
        {
            Assert.That(MapDefinition.Parse(FullJson).modelUrl, Is.EqualTo("mock/a.glb"));
        }

        [Test]
        public void Parse_Throws_WhenModelUrlIsMissing()
        {
            Assert.Throws<FormatException>(() => MapDefinition.Parse("{\"mapId\":\"x\"}"));
        }

        [Test]
        public void ToMapTransform_ReadsPositionAndScale()
        {
            var map = MapDefinition.Parse(FullJson).ToMapTransform();

            Assert.That(map.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(map.scale, Is.EqualTo(new Vector3(2f, 2f, 2f)));
        }

        [Test]
        public void ToMapTransform_NormalizesRotation()
        {
            var q = MapDefinition.Parse(FullJson).ToMapTransform().rotation;

            Assert.That(Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void ToMapTransform_ReturnsIdentity_WhenTransformIsOmitted()
        {
            var map = MapDefinition.Parse("{\"modelUrl\":\"a.glb\"}").ToMapTransform();

            Assert.That(map.position, Is.EqualTo(Vector3.zero));
            Assert.That(map.scale, Is.EqualTo(Vector3.one));
        }

        [Test]
        public void ToMapTransform_Throws_WhenPositionHasWrongLength()
        {
            var definition = MapDefinition.Parse("{\"modelUrl\":\"a.glb\",\"transform\":{\"position\":[1,2]}}");

            Assert.Throws<FormatException>(() => definition.ToMapTransform());
        }
    }
}
