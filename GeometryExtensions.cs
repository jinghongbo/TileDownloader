using NetTopologySuite.Geometries;
using ProjNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TileDownloader
{
    public static class GeometryExtensions
    {
        public static T Project<T>(this T source, int sourceSrid, int targetSrid) where T : Geometry
        {
            var services = new CoordinateSystemServices();
            var transformation = services.CreateTransformation(sourceSrid, targetSrid);
            var filter = new CoordinateSequenceFilter(transformation);
            var target = source.Copy();
            target.Apply(filter);
            target.SRID = targetSrid;
            return (T)target;
        }

        class CoordinateSequenceFilter : ICoordinateSequenceFilter
        {
            private ProjNet.CoordinateSystems.Transformations.ICoordinateTransformation transformation;

            public CoordinateSequenceFilter(ProjNet.CoordinateSystems.Transformations.ICoordinateTransformation transformation)
            {
                this.transformation = transformation;
            }

            public bool Done => true;

            public bool GeometryChanged => true;

            public void Filter(CoordinateSequence seq, int i)
            {
                var (x, y) = transformation.MathTransform.Transform(seq.GetX(i), seq.GetY(i));

                seq.SetX(i, x);
                seq.SetY(i, y);
            }
        }
    }
}
