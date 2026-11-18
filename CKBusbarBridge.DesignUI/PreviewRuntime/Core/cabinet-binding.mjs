import {DefinitionError} from './expressions.mjs';
import cabinetSources from '../catalog/cabinet-scene-sources.json' with {type:'json'};

export const cabinetScenePaths={'face-to-face':'模板/面对面模板/柜体装配.SLDASM','side-to-side':'模板/侧对侧模板/柜体装配.SLDASM'};
export const wallCabinetScenePaths={CabinetFrontToWall:'模板/面对墙模板(柜前向墙)/面对墙柜体装配.SLDASM',CabinetRearToWall:'模板/面对墙模板(柜后向墙)/面对墙柜体装配.SLDASM'};
export const cabinetScenePath=(scenario,record)=>scenario==='face-to-wall'?wallCabinetScenePaths[record?.orientation]:cabinetScenePaths[scenario];
const drivenPath=/^(?:General\.(?:CabinetSpacing|FloorHeight|ParameterB|CabinetEccentricity|CabinetTopOpeningMode)|[AB]Cabinet\.(?:CabinetHeight|CabinetDepth|CabinetWidth|GroundHeight)|[AB]Cabinet\.(?:FrontHole|BackHole)\.(?:IsEnabled|ToCabinetEdgeDistance|FixedHoleSpacing|L1|L2|L3))$/;
const wallDrivenPath=/^(?:General\.(?:CabinetToWallDistance|FloorToBeamDistance|WallHoleEdgeGroundHeight|WallHoleHeight|WallHoleWidth|BusbarSpec|CabinetGroundHeight|BuildingHeight|WallCabinetEccentricity|F1|F2|F3|J|WallBushingTerminalToWallDistance|WallEntryBusbarCenterToHoleBottomDistance)|General\.WallBushingTerminal\.(?:Diameter|C(?:[1-9]|10))|Cabinet\.(?:CabinetHeight|CabinetDepth|CabinetWidth)|Cabinet\.(?:FrontHole|BackHole)\.(?:IsEnabled|BaseToCabinetEdgeDistance|BusbarCenterToCabinetEdgeDistance|FixedHoleSpacing|L1|L2|L3))$/;

export function resolveCabinetSceneInstances(objects,inputs){
  return objects.map(object=>{
    if(!object.cabinetBinding){if(object.cabinetSceneInput)throw new DefinitionError('柜体实体参数缺少原业务组绑定');return object;}
    const binding=object.cabinetBinding,record=inputs[binding.scenario];
    const allowedPaths=binding.scenario==='face-to-wall'?Object.values(wallCabinetScenePaths):[cabinetScenePaths[binding.scenario]],source=cabinetSources.models.find(model=>model.path===object.templatePath),target=cabinetSources.models.find(model=>model.path===cabinetScenePath(binding.scenario,record));
    if(binding.version!=='original-cabinet-scene/1'||Object.keys(binding).some(key=>!['version','scenario'].includes(key))||!allowedPaths.includes(object.templatePath)||!source||object.sourceSha256!==source.sha256||object.configuration!==source.configuration||!target||object.category!=='cabinet'||object.parentId||!record||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length||object.hangerBinding||object.hangerDimensions||object.kind)throw new DefinitionError('柜体实体对象未对应唯一原场景业务组');
    const paths=binding.scenario==='face-to-wall'?wallDrivenPath:drivenPath;
    return {...object,templatePath:target.path,sourceSha256:target.sha256,configuration:target.configuration,cabinetSceneInput:{status:record.status,orientation:record.orientation,values:Object.fromEntries(Object.entries(record.values).filter(([path])=>paths.test(path)||/^General\.(?:InsulationDistance|BusbarSpec|SinglePhaseRootCount|PhaseSpacing|ExposedBusbarPhaseSpacing|[AB]CabinetExposedBusbarPhaseSequence)$/.test(path)||/\.(?:FrontHole|BackHole)\.BusbarCenterToCabinetEdgeDistance$/.test(path)))}};
  });
}
