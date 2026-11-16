import {DefinitionError} from './expressions.mjs';

// Sagitta, rather than edge length, controls a circular curve's approximation error.
export function circleSegmentsForTolerance(radiusMm,toleranceMm,minimumSegments=12){
  if(!Number.isFinite(radiusMm)||radiusMm<=0||!Number.isFinite(toleranceMm)||toleranceMm<=0)throw new DefinitionError('圆弧半径和轮廓弦差必须为有限正数');
  if(!Number.isInteger(minimumSegments)||minimumSegments<3||minimumSegments>720)throw new DefinitionError('曲面最少分段数必须为3至720之间的整数');
  const halfAngle=2*Math.asin(Math.sqrt(Math.min(1,toleranceMm/(2*radiusMm))));
  const segments=Math.max(minimumSegments,Math.ceil(Math.PI/halfAngle));
  if(segments>720)throw new DefinitionError('指定弦差需要超过720段，请增大轮廓弦差');
  return segments;
}

export function circleChordError(radiusMm,segments){return 2*radiusMm*Math.sin(Math.PI/(2*segments))**2;}

export function curveQualityLevels(settings){
  const initial=settings.complexArcSegments??settings.arcSegments??24,tolerance=settings.chordToleranceMm??.3;
  if(!Number.isInteger(initial)||initial<12||initial>720||!Number.isFinite(tolerance)||tolerance<=0)throw new DefinitionError('曲面分段数或轮廓弦差无效');
  return [
    {segments:initial,chordToleranceMm:tolerance},
    {segments:Math.max(12,Math.floor(initial*2/3)),chordToleranceMm:tolerance*2},
    {segments:12,chordToleranceMm:tolerance*4}
  ];
}
