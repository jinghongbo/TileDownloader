using NetTopologySuite.Geometries;
using ProjNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapDownloader
{
    public static class GeometryExtensions
    {
        public static T Projection<T>(this T source, int sourceSrid, int targetSrid) where T : Geometry
        {
            var services = new CoordinateSystemServices();
            var transformation = services.CreateTransformation(sourceSrid, targetSrid);
            var filter = new CoordinateSequenceFilter(transformation);
            var target = source.Copy();
            target.Apply(filter);
            target.SRID = targetSrid;
            return (T)target;
        }
        public static Envelope Projection(this Envelope envelope, int sourceSrid, int targetSrid)
        {
            var services = new CoordinateSystemServices();
            var transformation = services.CreateTransformation(sourceSrid, targetSrid);
            var (minX, minY) = transformation.MathTransform.Transform(envelope.MinX, envelope.MinY);
            var (maxX, maxY) = transformation.MathTransform.Transform(envelope.MaxX, envelope.MaxY);
            return new Envelope(minX, maxX, minY, maxY);
        }

        public static Polygon ToPolygon(this Envelope envelope)
        {
            return new Polygon(new LinearRing(new Coordinate[] {
                new Coordinate(envelope.MinX,envelope.MinY),
                new Coordinate(envelope.MinX,envelope.MaxY),
                new Coordinate(envelope.MaxX,envelope.MaxY),
                new Coordinate(envelope.MaxX,envelope.MinY),
                new Coordinate(envelope.MinX,envelope.MinY),
            }));
        }


        class CoordinateSequenceFilter : ICoordinateSequenceFilter
        {
            private readonly ProjNet.CoordinateSystems.Transformations.ICoordinateTransformation transformation;

            public CoordinateSequenceFilter(ProjNet.CoordinateSystems.Transformations.ICoordinateTransformation transformation)
            {
                this.transformation = transformation;
            }

            public bool Done => false;

            public bool GeometryChanged => true;

            public void Filter(CoordinateSequence seq, int i)
            {
                var x = seq.GetOrdinate(i, Ordinate.X);
                var y = seq.GetOrdinate(i, Ordinate.Y);
                (x, y) = transformation.MathTransform.Transform(x, y);
                seq.SetOrdinate(i, Ordinate.X, x);
                seq.SetOrdinate(i, Ordinate.Y, y);
            }
        }
    }
}
