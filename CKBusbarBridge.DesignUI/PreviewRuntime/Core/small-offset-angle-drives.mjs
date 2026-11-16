import {DefinitionError} from './expressions.mjs';

const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};
const target=equation=>/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];
const prefixes=['8GX.160.001','8GX.160.003'];
const nativeAngles=[{id:67,prefix:prefixes[0],end:-1},{id:79,prefix:prefixes[0],end:1},{id:75,prefix:prefixes[1],end:-1},{id:71,prefix:prefixes[1],end:1}];
export const sharedSmallOffsetAngleEquation=equation=>/^(?:D1@草图1@8GX\.160\.00[13]|b@Sketch1@8GX\.160\.001|D2@Sketch1@8GX\.160\.003)[.<-]/.test(target(equation)??'');

// Same per-end file plan as FaceToFaceSmallOffsetSealPlatePlanner.AddAngle.
// Preview document keys isolate instances; they are never native file paths.
export async function prepareSmallOffsetAngles(root,inner,height,right,inletWidth,outletWidth,{copies,readSource,drive,evidence,templateNotches=null}){
  root.equations=root.equations.filter(equation=>!sharedSmallOffsetAngleEquation(equation));
  const controlledPrefix=right?prefixes[0]:prefixes[1];
  for(const {id,prefix,end}of nativeAngles){
    const component=one(inner.components.filter(row=>row.id===id),'032原立角钢Id '+id);
    if(component.path!=='模板/基架模板/小偏移基架/'+prefix+'.SLDPRT'||component.configuration!=='默认<按加工>'||!Number.isFinite(component.transform?.[9])||Math.abs(component.transform[9]*1000-end*500)>1)throw new DefinitionError('032立角钢身份、配置或原安装端向不符：'+id);
    const nativePath=component.path,native=await readSource(nativePath);
    if(native.source.path!==nativePath||native.configurations.length!==1||native.configurations[0].name!==component.configuration)throw new DefinitionError('032立角钢原生初始配置与引用不一致');
    const template=structuredClone(native),instancePath=nativePath+'#preview-instance/'+component.id+'.SLDPRT';
    template.source={...template.source,path:instancePath,nativeTemplatePath:nativePath,previewInstanceId:component.id};
    copies.set(instancePath,template);component.path=instancePath;inner.businessComponentsChanged=true;
    const configuration=template.configurations[0],widthName=prefix===prefixes[0]?'b@Sketch1':'D2@Sketch1',width=end<0?inletWidth:outletWidth;
    const notch=one(configuration.features.filter(feature=>feature.name==='豁口'&&feature.type==='Cut'),'032原豁口');
    const capturedNotch=one(configuration.features.filter(feature=>feature.name==='草图4'),'032原豁口草图').dimensions.find(dimension=>dimension.name.startsWith('D2@草图4@'))?.value*1000;
    const values={'D1@草图1':Math.fround(height-45.5)};
    let suppressed=null;
    if(prefix===controlledPrefix&&width>2){
      values[widthName]=width>40&&width<=70?width:40;
      suppressed=width>=40;
      if(width<40)values['D2@草图4']=Math.fround(40-width);
    }else if(templateNotches!==null){
      if(typeof templateNotches!=='boolean'||!Number.isFinite(capturedNotch)||Math.abs(capturedNotch-22)>1)throw new DefinitionError('032通用豁口不是原22 mm模板');
      values['D2@草图4']=22;suppressed=templateNotches;
    }
    await drive(component,values);
    if(suppressed!==null){
      if(configuration.equations.some(equation=>target(equation)===notch.name))throw new DefinitionError('032豁口已有冲突的原压缩方程');
      configuration.equations.push({index:configuration.equations.length,expression:JSON.stringify(notch.name)+' = '+JSON.stringify(suppressed?'suppressed':'unsuppressed'),globalVariable:false,disabled:false,businessDriveSource:'original-programmatic-driver'});
    }
    evidence.push({operation:'independent-angle-instance',nativeTemplatePath:nativePath,templatePath:instancePath,componentId:component.id,component:component.name,end,values,suppressNotch:suppressed,source:'original-programmatic-driver'});
  }
}
