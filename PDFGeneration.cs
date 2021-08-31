// This is a software to generated pdf files
// Copyright(C) 2021  AlignmentHealthplan         
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or(at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU Affero General Public License for more details.
//You should have received a copy of the GNU Affero General Public License
//along with this program.If not, see<http://www.gnu.org/licenset>



using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Resources;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Files.DataLake;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Events;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas.Draw;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.XMP;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Renderer;
using iText.License;
using iText.Pdfoptimizer;
using iText.Pdfoptimizer.Handlers;
using iText.Pdfoptimizer.Handlers.Imagequality.Processors;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using ProviderDirectoryService.Common;
using ProviderDirectoryService.Module;
using Document = iText.Layout.Document;

namespace ProviderDirectoryService
{
    public class PDFGeneration
    {

      

        [FunctionName("PDFGeneration")]
        public static async Task<HttpResponseMessage> HttpStart(
          [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
          [DurableClient] IDurableOrchestrationClient starter,
          ILogger log,
          ExecutionContext context)
        {
            try
            {
                log.LogInformation("PD-PDFGeneration - begin");
                Helper helper = new Helper();
                helper.InitLog(log, context.FunctionAppDirectory);
                var RuntimeStatus = await helper.GetInstanceStatusAsync(starter);
                if (RuntimeStatus == null || RuntimeStatus.Status == "Completed")
                {

                    var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
                    string versionNo = query.Get("version");
                    if (string.IsNullOrEmpty(versionNo))
                    {
                        versionNo = "1.0";
                    }
                    APIContext aPIContext = helper.GetAPIContext();
                    var sqlClient = new EdwSqlClient(aPIContext);
                    var dapperContext = new DapperContext(aPIContext, sqlClient);
                    PDConfigurationManager pdConfig = await helper.GetLastRunDate(log, versionNo, aPIContext, dapperContext);
                    List<TemplateDetails> pendingCounty = await dapperContext.GetTemplateDetailsMasterS2Async();
                    RequestParameter objParam = new RequestParameter
                    {
                        TemplateDetails = pendingCounty.Where(a => a.TemplateId=="2" && a.Language=="Chinese").ToList(),
                        aPIContext = aPIContext,
                        Version = decimal.Parse(versionNo),
                        LastRunDate = pdConfig.LastRunDate
                    };

                    string instanceId = await starter.StartNewAsync("PDFGenerationOrchestrator", objParam);
                    log.LogInformation($"PD-PDFGeneration - Started new instance with ID '{instanceId}'.");
                    StorageTableOperation<PDConfigurationManager> tableObj = new StorageTableOperation<PDConfigurationManager>("PDConfig", aPIContext.AzureWebJobsStorage);
                    PDConfigurationManager pdconfigDataStorage = tableObj.GetTableData("ConfigValue", versionNo);
                    pdconfigDataStorage.InstanceId = instanceId;
                    pdconfigDataStorage.OverAllStatus = "Running";
                    await tableObj.UpdateTableData("LastRunDate", versionNo, pdconfigDataStorage);
                    return starter.CreateCheckStatusResponse(req, instanceId);
                }
                else
                {
                    if (RuntimeStatus != null && RuntimeStatus.InstanceID != null)
                    {
                        log.LogInformation($"PD-PDFGeneration - Instance already running ID = '{RuntimeStatus.InstanceID}'.");
                        return starter.CreateCheckStatusResponse(req, RuntimeStatus.InstanceID);
                    }
                    else
                    {
                        log.LogInformation("PD-PDFGeneration - Instance already running and instance ID empty");
                        return starter.CreateCheckStatusResponse(req, Guid.NewGuid().ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex.StackTrace);
                throw ex;
            }

        }

        [FunctionName("PDFGenerationOrchestrator")]
        public static async Task<bool> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            try
            {
                log.LogInformation("PD-PDFGenerationOrchestrator - Started");
                RequestParameter objParm = context.GetInput<RequestParameter>();
                var sublist = objParm.TemplateDetails.SplitList(1);
                log.LogInformation($"PD-PDFGenerationOrchestrator - Number of PDF to be generated '{sublist?.Count()}'.");
                var tasks = sublist.Select((a, index) => context.CallSubOrchestratorAsync<object>(
                        "PDFGenerationSubOrchestrator",
                    new RequestParameter
                    {
                        aPIContext = objParm.aPIContext,
                        LastRunDate = objParm.LastRunDate,
                        Version = objParm.Version,
                        sublist = a
                    })).ToList();
                await Task.WhenAll(tasks);
                var result = await context.CallActivityAsync<bool>("UpdateCountyListActivity", objParm);
                return true;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        [FunctionName("PDFGenerationSubOrchestrator")]
        public static async Task<object> RunSubOrchestrator(
         [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            try
            {
                log.LogInformation("PD-PDFGenerationSubOrchestrator - Started");
                var retryOptions = new RetryOptions(
                                               firstRetryInterval: TimeSpan.FromMinutes(1),
                                               maxNumberOfAttempts: 1);
                RequestParameter objParm = context.GetInput<RequestParameter>();
                var tasks = objParm.sublist.Select((a, index) =>
                context.CallActivityWithRetryAsync<object>("PDFGenerationActivity", retryOptions,
                    new RequestParameter
                    {
                        aPIContext = objParm.aPIContext,
                        LastRunDate = objParm.LastRunDate,
                        Version = objParm.Version,
                        TemplateAssign = a,
                    }
                    )).ToList();
                await Task.WhenAll(tasks);
                var result = tasks.Select(a => a.Result).ToList();
                return result;
            }
            catch (Exception ex)
            {
                log.LogError(ex.StackTrace);
                throw ex;
            }
        }

        [FunctionName("PDFGenerationActivity")]
        public async Task<string> PDFGenerationActivity([ActivityTrigger] IDurableActivityContext context,
            ILogger log, ExecutionContext Econtext)
        {
            log.LogInformation("PD-PDFGenerationActivity - Started");
            string currentTitle = string.Empty;
            List<Pair<String, Pair<String, int>>> toc = new List<Pair<string, Pair<string, int>>>();
            string fileSystemName = "gold";
            string filenamepdf = Guid.NewGuid().ToString();
            try
            {

                List<Provider> providers = new List<Provider>();
                List<ProviderCategory> lstPCategroy = new List<ProviderCategory>();
                Helper helper = new Helper();
                RequestParameter param = context.GetInput<RequestParameter>();


                log.LogInformation("PD-PDFGenerationActivity - Init Object Started");
                AHCFonts fonts = new AHCFonts(Econtext.FunctionAppDirectory);
                helper.InitLog(log, Econtext.FunctionAppDirectory);
                APIWrapper apiWrapper = new APIWrapper(log, param.aPIContext);
                var sqlClient = new EdwSqlClient(param.aPIContext);
                var dapperContext = new DapperContext(param.aPIContext, sqlClient);
                log.LogInformation("PD-PDFGenerationActivity - Init Object Completed");

                log.LogInformation("PD-PDFGenerationActivity - Loading template config details");
                List<PDTemplateDetail> TemplateDetailsS1 = await dapperContext.GetTemplateDetailsFromEDWConfigDBAsync(new ParameterDetails() { Language = param.TemplateAssign.Language, Type = param.TemplateAssign.LOB, Year = param.TemplateAssign.Year });
                List<ProviderConfig> provconfigs = await dapperContext.GetProviderDirectoryConfig(new ParameterDetails() { Language = param.TemplateAssign.Language, TemplateId = param.TemplateAssign.TemplateId, Year = param.TemplateAssign.Year });
                log.LogInformation("PD-PDFGenerationActivity - Loaded template config details");

                Dictionary<string, Dictionary<string, int>> ProviderSummary = new Dictionary<string, Dictionary<string, int>>();
                foreach (ProviderConfig provconfig in provconfigs.Where(q=>q.ChapterId==6))
                {
                    log.LogInformation($"PD-PDFGenerationActivity - GetProvider Started for '{provconfig.ChapterName}'");
                    providers = await apiWrapper.GetProvider(provconfig);
                    log.LogInformation($"PD-PDFGenerationActivity - GetProvider Completed");
                    if (providers != null)
                    {
                        List<ProviderResponse> normalisePrv = Helper.NormaliseProviderData(providers, TemplateDetailsS1.FirstOrDefault().Language); //.Where(a=>a.USPSCity.ToUpper()== "HUNTINGTN BCH").ToList();                       
                        ProviderCategory category = Helper.MapProviderType(normalisePrv, provconfig, ProviderSummary);
                        log.LogInformation($" PD-PDFGenerationActivity -  {provconfig.ChapterName} - {normalisePrv.Count()} records");
                        category.Config = provconfig;
                        lstPCategroy.Add(category);
                    }
                }

                if (lstPCategroy.Count > 0)
                {
                    log.LogInformation($"PD-PDFGenerationActivity - Create Pdf Started");
                    var pDFResult = CreatePdf(lstPCategroy, ProviderSummary, TemplateDetailsS1, helper, Econtext.FunctionAppDirectory, param.TemplateAssign.Language, log);
                    log.LogInformation($"PD-PDFGenerationActivity - Create Pdf Completed");
                    if (!pDFResult.Failed)
                    {

                        log.LogInformation($"PD-PDFGenerationActivity - SaveFile Started");
                        string filepathProvDirect = "providerdirectory/" + DateTime.Now.ToString("yyyy") + "/" + DateTime.Now.ToString("MMMM") + "/" + DateTime.Now.ToString("dd");
                        DataLakeDirectoryClient directoryClientProvDirect = GetADLSDirectClient(fileSystemName, param, filepathProvDirect, log);
                        await SavePDFInADLS(pDFResult.PdfStream, DateTime.Now.ToString("MMyy")+"_"+provconfigs.FirstOrDefault().CompanyId.ToUpper()+"_"+provconfigs.FirstOrDefault().TemplateName + "_" + DateTime.Now.ToString("MMddyy")+"_"+ param.TemplateAssign.Language, log, directoryClientProvDirect);
                        log.LogInformation($"PD-PDFGenerationActivity - SaveFile Completed");

                    }
                    else
                    {
                        log.LogInformation($"PD-PDFGenerationActivity - Create Pdf Failed");
                        throw new CustomException($"PD-PDFGenerationActivity Failed Tempalte ID :'{param.TemplateAssign.TemplateId}', Error Message : '{pDFResult.ErrorMessage}'");
                    }


                }
                else
                {
                    log.LogInformation($"PD-PDFGenerationActivity - There is no data returns from API");
                    throw new CustomException($"PD-PDFGenerationActivity - Failed Tempalte ID:'{param.TemplateAssign.TemplateId}'");
                }

                log.LogInformation($"PD-PDFGenerationActivity - '{ param.TemplateAssign.TemplateId }' Completed");

                return param.TemplateAssign.TemplateId + " Success";
            }
            catch (Exception ex)
            {
                log.LogError(ex.StackTrace);
                throw ex;
            }
        }



        private async Task SavePDFInADLS(byte[] vb1, string filename, ILogger log, DataLakeDirectoryClient directoryClient)
        {
            try
            {
                log.LogInformation("PD-SavePDFInADLS - Started");
                await Task.Run(() =>
                {

                    DataLakeFileClient fileClient = directoryClient.CreateFile(filename + ".pdf");
                    lock (fileClient)
                    {

                        Stream stream = new MemoryStream(vb1);
                        long fileSize = stream.Length;
                        fileClient.Append(stream, offset: 0);
                        fileClient.Flush(position: fileSize);
                    }

                });
                log.LogInformation("PD-SavePDFInADLS - Completed");
            }
            catch (Exception ex)
            {
                log.LogError("PD-SavePDFInADLS function failed" + ex.StackTrace);
            }
        }


        public static PDFResult CreatePdf(List<ProviderCategory> ProvLst, Dictionary<string, Dictionary<string, int>> providerSummary, List<PDTemplateDetail> TemplateSection1, Helper Utility, string functionAppDirectory, string Language, ILogger log)
        {

            List<Pair<String, Pair<String, int>>> toc = new List<Pair<string, Pair<string, int>>>();
            var stream = new MemoryStream();
            var writer = new PdfWriter(stream);
            writer.SetCompressionLevel(CompressionConstants.BEST_COMPRESSION);
            var pdf = new PdfDocument(writer);

            PageSize ps = Helper.PdfPageSize;
            var document = new Document(pdf, ps);
            document.SetProperty(Property.LEADING, new Leading(Leading.MULTIPLIED, 1.08f));
            document.SetMargins(55, 36, 75, 36);
            PdfOutline root = pdf.GetOutlines(false);
            PDFResult result = new PDFResult();
            ResourceSet cultureResourceSet = null;

            try
            {

                AHCStyles style = new AHCStyles(Language);
                string ResourceFilename = Helper.GetResourceFileName(Language);
                cultureResourceSet = new ResourceSet(Assembly.GetExecutingAssembly().GetManifestResourceStream("ProviderDirectoryService.Resources." + ResourceFilename + ".resources"));

                if (cultureResourceSet != null)
                {
                    log.LogInformation("PD-CreatePdf - Started");
                    //SetTagged
                    pdf.GetCatalog().SetLang(new PdfString("en-US"));
                    pdf.SetXmpMetadata(XMPMetaFactory.Create());
                    pdf.SetTagged();
                    pdf.GetCatalog().SetViewerPreferences(new PdfViewerPreferences().SetDisplayDocTitle(true).SetFitWindow(true));

                    log.LogInformation("PD-CreatePdf - Adding metadata Information");
                    //Set Document Information
                    PdfDocumentInfo info = pdf.GetDocumentInfo();
                    info.SetTitle(DateTime.Now.Year.ToString() + " " + cultureResourceSet.GetString("ProviderDirectory").ToUpper() + ": " + ProvLst.FirstOrDefault().Config.CoverHeading);
                    info.SetAuthor("Alignment Health Plan");
                    info.SetSubject(DateTime.Now.Year.ToString() + " " + cultureResourceSet.GetString("ProviderDirectory").ToUpper() + ": " + ProvLst.FirstOrDefault().Config.CoverHeading);
                    info.SetKeywords(DateTime.Now.Year.ToString() + " " + cultureResourceSet.GetString("ProviderDirectory").ToUpper() + ": " + ProvLst.FirstOrDefault().Config.CoverHeading + ",Alignment Health Plan");
                    info.SetCreator("Alignment Health Plan");

                    //EventHandler
                    var headerEventhandler = new TableHeaderEventHandler(document);
                    pdf.AddEventHandler(PdfDocumentEvent.END_PAGE, headerEventhandler);
                    var footerEventHandler = new TableFooterEventHandler(document);
                    pdf.AddEventHandler(PdfDocumentEvent.END_PAGE, footerEventHandler);

                  



                    //Section 1 Binding
                    int ICounter = 0;

                    PdfOutline ODirectory = null;

                    PdfOutline intro = null;


                    ImageData data = ImageDataFactory.Create(functionAppDirectory + "/images/ahclogo.png");
                    Image pdfImg = new Image(data);
                    pdfImg.GetAccessibilityProperties().SetActualText("AHC Logo");
                    pdfImg.SetHorizontalAlignment(HorizontalAlignment.CENTER);
                    pdfImg.GetAccessibilityProperties().SetAlternateDescription("AHC Logo");
                    pdfImg.SetStrokeColor(ColorConstants.LIGHT_GRAY);
                    pdfImg.SetTextAlignment(TextAlignment.CENTER);
                    pdfImg.ScaleToFit(192, 192);
                    document.Add(pdfImg);

                    Paragraph p = new Paragraph();
                    p.SetMarginTop(20);
                    p.Add(DateTime.Now.Year.ToString()).AddStyle(AHCStyles.LogoHeader);
                    p.SetTextAlignment(TextAlignment.CENTER);
                    document.Add(p);

                    p = new Paragraph();
                    p.Add(cultureResourceSet.GetString("ProviderDirectory").ToUpper()).AddStyle(AHCStyles.LogoHeader);
                    p.SetFixedLeading(0);
                    p.SetPaddingBottom(30);
                    p.SetTextAlignment(TextAlignment.CENTER);
                    document.Add(p);


                    footerEventHandler.ShowPageNo = false;
                    foreach (PDTemplateDetail template in TemplateSection1)
                    {
                        if (ICounter != 0)
                        {
                            document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                            document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));


                            float toffSet = 45;
                            float tcolumnWidth = (ps.GetWidth() - (toffSet * 2 + 45)) / 2;
                            float tcolumnHeight = ps.GetHeight() - (75 + 35);
                            Rectangle[] twocolumns = {new Rectangle(toffSet - 5, toffSet, tcolumnWidth, tcolumnHeight),
                            new Rectangle(toffSet + tcolumnWidth+30, toffSet, tcolumnWidth, tcolumnHeight),
                           };
                            DocumentRenderer twocolumnsrender = new ColumnDocumentRenderer(document, twocolumns);
                            document.SetRenderer(twocolumnsrender);

                        }
                        footerEventHandler.CultureResourceSet = cultureResourceSet;
                        ProvLst.FirstOrDefault().Config.Lanaguage = TemplateSection1.FirstOrDefault().Language;
                        footerEventHandler.Config = ProvLst.FirstOrDefault().Config;
                        footerEventHandler.DisClaimer = template.Disclaimer;
                        Utility.CreatePage(document, template.PageContent, ProvLst.FirstOrDefault().Config, cultureResourceSet);


                        if (ICounter == 0)
                        {
                            footerEventHandler.ShowPageNo = true;
                            headerEventhandler.HeaderText = cultureResourceSet.GetString("Introduction").ToUpper();
                            ODirectory = Helper.CreateOutline(root, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), DateTime.Now.Year + " " + cultureResourceSet.GetString("ProviderDirectory").ToUpper() + ": " + ProvLst.FirstOrDefault().Config.CoverHeading);

                        }
                        if (ICounter == 1)
                        {
                            footerEventHandler.ShowPageNo = true;
                            headerEventhandler.HeaderText = cultureResourceSet.GetString("Introduction").ToUpper();
                            intro = Helper.CreateOutline(ODirectory, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("Introduction").ToUpper());
                            intro.SetOpen(false);
                        }
                        if (ICounter == 2)
                        {
                            Helper.CreateOutline(intro, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("ServiceArea"));
                            Helper.CreateOutline(intro, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("HowDoYouFindAHCProviders"));
                        }

                        ICounter++;

                    }

                    log.LogInformation("PD-CreatePdf - Populate Summary ");

                    var lst = providerSummary.Keys.ToList();
                    lst.Sort();





                    foreach (string key in lst)
                    {

                        Table tblSummary = new Table(1);
                        tblSummary.UseAllAvailableWidth();
                        tblSummary.SetBorderCollapse(BorderCollapsePropertyValue.SEPARATE);
                        tblSummary.SetVerticalBorderSpacing(2);
                        tblSummary.SetHorizontalBorderSpacing(2);
                        tblSummary.GetAccessibilityProperties().SetRole(StandardRoles.DIV);
                        Cell cell1 = new Cell();
                        cell1.SetBorder(Border.NO_BORDER);
                        cell1.SetPadding(0);
                        cell1.SetMargin(0);
                        cell1.GetAccessibilityProperties().SetRole(StandardRoles.P);
                        Paragraph summary = new Paragraph();
                        string keyValue = key;
                        foreach (string cmpid in ProvLst.FirstOrDefault().Config.CompanyId.Split(","))
                        {
                            keyValue = keyValue.Replace(cmpid + "-", " ")
                                                .Replace("Nevada-", "")
                                                .Replace("AHP", "California")
                                                .Replace("NEVA", "Nevada")
                                                .Replace("NOCA", "North Carolina")
                                                .Replace("AZHMO", "Arizona")
                                                .Replace("AZPPO", "Arizona")
                                                .Replace("DCE", "Acceleran Direct");
                        }
                        keyValue = keyValue + ":";
                        summary.Add(keyValue);
                        summary.AddStyle(AHCStyles.SummaryBold);
                        cell1.Add(summary);
                        cell1.SetKeepWithNext(true);
                        tblSummary.AddHeaderCell(cell1);

                        foreach (var chapter in providerSummary[key])
                        {
                            cell1 = new Cell();
                            cell1.GetAccessibilityProperties().SetRole(StandardRoles.P);
                            cell1.SetBorder(Border.NO_BORDER);
                            cell1.SetPadding(0);
                            cell1.SetMargin(0);

                            summary = new Paragraph();
                            Text t = new Text(chapter.Key + "\u00a0");
                            t.AddStyle(AHCStyles.CategorySummary);
                            summary.Add(t);


                            t = new Text(string.Format("{0:#,0}", chapter.Value));
                            t.AddStyle(AHCStyles.SummaryBold);
                            summary.Add(t);

                            cell1.Add(summary);
                            tblSummary.AddCell(cell1);
                        }

                        cell1 = new Cell();
                        cell1.SetBorder(Border.NO_BORDER);
                        cell1.SetPaddingBottom(2);
                        cell1.GetAccessibilityProperties().SetRole(StandardRoles.P);
                        tblSummary.AddCell(cell1);
                        document.Add(tblSummary);

                    }
                    log.LogInformation("PD-CreatePdf - Section 1 Template Completed");

                    log.LogInformation("PD-CreatePdf - Section 2 Template Started");

                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                    footerEventHandler.ShowPageNo = true;
                    footerEventHandler.DisClaimer = string.Empty;
                    headerEventhandler.HeaderText = cultureResourceSet.GetString("ListofNetworkProvider").ToUpper();// "LIST OF NETWORK PROVIDERS";
                    var OnetworkProv = Helper.CreateOutline(ODirectory, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("ListofNetworkProvider").ToUpper());
                    OnetworkProv.SetOpen(false);


                    //Section 2
                    //if (false)
                    //{
                    #region section 2

                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                    headerEventhandler.HeaderText = string.Empty;
                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));


                    float offSet = 45;
                    float columnWidth = (ps.GetWidth() - (offSet * 2 + 5)) / 3;
                    float columnHeight = ps.GetHeight() - (75 + 35);
                    Rectangle[] columns = {new Rectangle(offSet - 5, offSet, columnWidth, columnHeight),
                             new Rectangle(offSet + columnWidth, offSet, columnWidth, columnHeight),
                             new Rectangle(offSet + columnWidth * 2 + 5, offSet, columnWidth, columnHeight)};
                    DocumentRenderer myrender = new ColumnDocumentRenderer(document, columns);
                    document.SetRenderer(myrender);
                    Dictionary<string, List<ProviderResponse>> dicpage = new Dictionary<string, List<ProviderResponse>>();
                    Dictionary<string, List<string>> pvIndex = new Dictionary<string, List<string>>();




                    int prvIndex = 0;
                    Pair<String, int> titlePage;
                    Paragraph objParagraph = new Paragraph();
                    PdfOutline CountyOutline = null;
                    string TemVar = string.Empty;
                    foreach (var category in ProvLst)
                    {


                        ProviderConfig config = category.Config;

                        if (prvIndex != 0)
                        {

                            document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                            titlePage = new Pair<String, int>(config.TOCHeading, myrender.GetCurrentArea().GetPageNumber() + 1);
                            toc.Add(new Pair<String, Pair<String, int>>(config.TOCHeading, titlePage));
                        }
                        else
                        {
                            // CountyOutline = Helper.CreateOutline(OnetworkProv, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), config.IPAHeader.ToUpper() + "," +category.CountyList.FirstOrDefault().Name.ToUpper());
                            titlePage = new Pair<String, int>(config.TOCHeading, myrender.GetCurrentArea().GetPageNumber() + 1);
                            toc.Add(new Pair<String, Pair<String, int>>(config.TOCHeading, titlePage));
                        }

                        prvIndex++;


                        log.LogInformation(config?.IPAHeader);


                        if (!string.IsNullOrEmpty(config.Description) && config.ShowIPADescription)
                        {
                            objParagraph = new Paragraph(config.IPAHeader.ToUpper()).SetDestination(config.TOCHeading).AddStyle(AHCStyles.IPAHeader);
                            objParagraph.SetFontColor(ColorConstants.WHITE);
                            objParagraph.SetBackgroundColor(ColorConstants.BLACK);
                            objParagraph.SetTextAlignment(TextAlignment.CENTER);
                            objParagraph.SetPadding(5f);
                            objParagraph.SetMarginBottom(0f);
                            document.Add(objParagraph);

                            objParagraph = new Paragraph(config.Description).AddStyle(AHCStyles.IPAHeader);
                            objParagraph.GetAccessibilityProperties().SetRole(StandardRoles.P);
                            objParagraph.SetFontColor(ColorConstants.BLACK);
                            objParagraph.SetBackgroundColor(ColorConstants.LIGHT_GRAY);
                            objParagraph.SetMarginTop(2f);
                            objParagraph.SetPadding(4f);
                            document.Add(objParagraph);
                        }
                        else
                        {
                            objParagraph = new Paragraph().SetDestination(config.TOCHeading);
                            objParagraph.SetMarginTop(-1f);
                            objParagraph.SetPadding(0f);
                            objParagraph.SetBorder(Border.NO_BORDER);
                            document.Add(objParagraph);
                        }


                        int countyix = 0;
                        #region Generate PDF

                        foreach (var county in category.CountyList)
                        {
                            Div dv = new Div();
                            int j = 0;

                            if (countyix != 0)
                            {
                                document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                            }

                            CountyOutline = Helper.CreateOutline(OnetworkProv, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), county.Name.Replace("AHP", cultureResourceSet.GetString("AllCounties").ToUpper() + ", CA").Replace("NEVA", cultureResourceSet.GetString("AllCounties").ToUpper() + ", NV").ToUpper() + "," + config.IPAHeader.ToUpper());
                            CountyOutline.SetOpen(false);
                            if (config.GroupByCity)
                            {
                                foreach (var city in county.CityList.Where(a => !string.IsNullOrEmpty(a.Name)))
                                {
                                    int cityix = 0;
                                    if (config.GroupBySpeciality)
                                    {
                                        string pdfname = Guid.NewGuid().ToString();

                                        objParagraph = new Paragraph(city.Name.ToUpper());
                                        objParagraph.SetDestination(pdfname);
                                        objParagraph.GetAccessibilityProperties().SetRole(StandardRoles.P);
                                        objParagraph.SetFontColor(ColorConstants.WHITE);
                                        objParagraph.SetBackgroundColor(ColorConstants.BLACK);
                                        objParagraph.SetTextAlignment(TextAlignment.CENTER);

                                        objParagraph.SetPadding(2f);
                                        objParagraph.SetMarginBottom(0f);
                                        objParagraph.SetKeepWithNext(true);
                                        document.Add(objParagraph);

                                    }
                                    else
                                    {
                                        dv = new Div();

                                        objParagraph = new Paragraph(city.Name.ToUpper());
                                        objParagraph.SetFont(AHCFonts.TimesRoman_Bold);
                                        objParagraph.SetMarginBottom(0f);
                                        dv.Add(objParagraph);

                                        SolidLine line = new SolidLine();
                                        line.SetColor(ColorConstants.BLACK);
                                        LineSeparator ls = new LineSeparator(line);
                                        ls.GetAccessibilityProperties().SetRole(StandardRoles.ARTIFACT);
                                        dv.Add(ls);

                                    }

                                    if (config.GroupBySpeciality)
                                    {
                                        int kj = 0;

                                        foreach (var spl in city.SpecialitiesList.Where(a => !string.IsNullOrEmpty(a.Name)))
                                        {
                                            if (j != 0)
                                            {
                                                dv = new Div();
                                            }

                                            objParagraph = new Paragraph(spl.Name.ToUpper());
                                            objParagraph.SetMarginBottom(0f);
                                            objParagraph.SetFont(AHCFonts.TimesRoman_Bold);
                                            dv.Add(objParagraph);

                                            SolidLine line = new SolidLine();
                                            line.SetColor(ColorConstants.BLACK);
                                            LineSeparator ls = new LineSeparator(line);
                                            ls.GetAccessibilityProperties().SetRole(StandardRoles.ARTIFACT);
                                            dv.Add(ls);

                                            if (spl.HospitalList != null) //other care facilities
                                            {
                                                int hj = 0;

                                                foreach (HospitalList hpl in spl.HospitalList)
                                                {

                                                    Text Name;
                                                    Dictionary<string, string> addressduplicate = new Dictionary<string, string>();
                                                    int ix = 0;
                                                    foreach (ProviderResponse response in hpl.ProviderList)
                                                    {

                                                        if (hj != 0)
                                                        {
                                                            dv = new Div();
                                                            objParagraph = new Paragraph();
                                                        }
                                                        if (ix == 0)
                                                        {
                                                            objParagraph = new Paragraph();
                                                            Name = new Text(hpl.ProviderList.FirstOrDefault().NPPESOrganizationName).AddStyle(AHCStyles.ProvName);
                                                            objParagraph.Add(Name);
                                                            objParagraph.Add("\n");
                                                            objParagraph.SetDestination(hpl.ProviderList.FirstOrDefault().NPPESOrganizationName);
                                                        }
                                                        string key = response.USPSAddress1 + " " + response.USPSAddress2 + " " + response.USPSCity + ", " +
                                                                             response.USPSState + " " +
                                                                             response.USPSZip5;
                                                        if (!addressduplicate.ContainsKey(key))
                                                        {


                                                            addressduplicate.Add(key, response.NPPESOrganizationName);

                                                            if (!string.IsNullOrEmpty(response.USPSAddress1))
                                                            {
                                                                objParagraph.Add(new Text(response.USPSAddress1).AddStyle(AHCStyles.Address));
                                                                if (!string.IsNullOrEmpty(response.USPSAddress2))
                                                                    objParagraph.Add(new Text("\u00a0" + response.USPSAddress2).AddStyle(AHCStyles.Address));
                                                                objParagraph.Add("\n");
                                                            }
                                                            objParagraph.Add(new Text(response.USPSCity + ", " +
                                                                                 response.USPSState + " " +
                                                                                 response.USPSZip5).AddStyle(AHCStyles.Address)).Add("\n");
                                                            if (Helper.ValidPhone(response.Phone))
                                                            {
                                                                objParagraph.Add(new Text(Convert.ToInt64(response.Phone).ToString("(###) ###-####")).AddStyle(AHCStyles.Address));
                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(new Text(response.Phone).AddStyle(AHCStyles.Address));

                                                            }
                                                            objParagraph.Add("\n");


                                                            objParagraph.SetMultipliedLeading(1.0f);
                                                            dv.Add(objParagraph);
                                                            dv.SetKeepTogether(true);
                                                            document.Add(dv);
                                                            if (kj == 0)
                                                            {
                                                                Helper.CreateOutline(CountyOutline, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), city.Name.ToUpper());
                                                            }
                                                            kj++;

                                                            var objresponse = new ProviderResponse();
                                                            objresponse.CategoryName = config.PageHeader;
                                                            objresponse.City = city.Name;
                                                            objresponse.County = county.Name;
                                                            objresponse.SiteHeading = config.SiteHeading;
                                                            objresponse.SkipFooter = false;
                                                            objresponse.SkipHeader = false;
                                                            objresponse.IsIndexPage = false;
                                                            objresponse.SkipDisclaimer = true;

                                                            if (dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber().ToString())))
                                                            {
                                                                dicpage[myrender.GetCurrentArea().GetPageNumber().ToString()].Add(objresponse);
                                                            }
                                                            else
                                                            {
                                                                dicpage.Add(myrender.GetCurrentArea().GetPageNumber().ToString(), new List<ProviderResponse>() { objresponse });
                                                            }
                                                            
                                                            headerEventhandler.skipCity = false;
                                                            headerEventhandler.HeaderText = string.Empty;
                                                            headerEventhandler.CultureResourceSet = cultureResourceSet;
                                                            footerEventHandler.CultureResourceSet = cultureResourceSet;
                                                            headerEventhandler.entry = dicpage;
                                                            footerEventHandler.entry = dicpage;



                                                            if (!string.IsNullOrEmpty(response.NPPESOrganizationName))
                                                            {

                                                                if (pvIndex.ContainsKey(response.NPPESOrganizationName))
                                                                {
                                                                    pvIndex[response.NPPESOrganizationName].Add((myrender.GetCurrentArea().GetPageNumber() + 1).ToString());
                                                                }
                                                                else
                                                                {
                                                                    pvIndex.Add(response.NPPESOrganizationName, new List<string>() { (myrender.GetCurrentArea().GetPageNumber() + 1).ToString() });

                                                                }
                                                            }




                                                        }
                                                        hj++;

                                                        ix++;
                                                    }


                                                }
                                            }
                                            else
                                            {
                                                int i = 0;
                                                foreach (IPAList prv in spl.IPAList)
                                                {

                                                    // currentTitle = prv.Name;//\u002A                                   
                                                    Dictionary<string, int> entry = new Dictionary<string, int>();
                                                    Text symbol = new Text("v").AddStyle(AHCStyles.Symbol);
                                                    Text Name;



                                                    for (int index = 0; index < prv.ProviderList.Count; index++)
                                                    {
                                                        if (i != 0)
                                                        {
                                                            dv = new Div();
                                                        }
                                                        var addnewitem = false;
                                                        objParagraph = new Paragraph();
                                                        var response = prv.ProviderList[index];

                                                        if (index == 0)
                                                        {
                                                            addnewitem = true;

                                                         
                                                                if (!string.IsNullOrEmpty(response.FullName))
                                                                {
                                                                    if (pvIndex.ContainsKey(response.FullName))
                                                                    {
                                                                        pvIndex[response.FullName].Add((myrender.GetCurrentArea().GetPageNumber() + 1).ToString());
                                                                    }
                                                                    else
                                                                    {
                                                                        pvIndex.Add(response.FullName, new List<string>() { (myrender.GetCurrentArea().GetPageNumber() + 1).ToString() });

                                                                    }
                                                                }
                                                            
                                                            Name = new Text(response.FullName).AddStyle(AHCStyles.ProvName);
                                                            objParagraph.SetDestination(response.FullName);

                                                            if (response.IsMedicaidRegistered.ToLower().Equals("yes"))
                                                            {
                                                                objParagraph.Add(symbol).Add(Name).Add("\n");
                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(Name).Add("\n");
                                                            }

                                                            if (!string.IsNullOrEmpty(response.USPSAddress1))
                                                            {
                                                                objParagraph.Add(new Text(response.USPSAddress1).AddStyle(AHCStyles.Address));
                                                                if (!string.IsNullOrEmpty(response.USPSAddress2))
                                                                    objParagraph.Add(new Text("\u00a0" + response.USPSAddress2).AddStyle(AHCStyles.Address));
                                                                objParagraph.Add("\n");
                                                            }
                                                            objParagraph.Add(new Text(response.USPSCity + ", " + response.USPSState + " " + response.USPSZip5).AddStyle(AHCStyles.Address)).Add("\n");
                                                            if (Helper.ValidPhone(response.Phone))
                                                            {
                                                                objParagraph.Add(new Text(Convert.ToInt64(response.Phone).ToString("(###) ###-####")).AddStyle(AHCStyles.Address));
                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(new Text(response.Phone).AddStyle(AHCStyles.Address));

                                                            }
                                                            objParagraph.Add("\n");
                                                            if (!string.IsNullOrEmpty(string.Join(", ", response.otherLanguages)))
                                                                objParagraph.Add(new Text(string.Join(", ", response.otherLanguages)).AddStyle(AHCStyles.Address)).Add("\n");


                                                            Text network = new Text(response.IPA).AddStyle(AHCStyles.IPAName);
                                                            objParagraph.Add(network).Add("\n")
                                                             .Add(new Text("\u00a0\u00a0Provider ID: " + response.ProvID).AddStyle(AHCStyles.Address));

                                                            if (response.IsAcceptingNewPatients.ToLower().Equals("yes"))
                                                            {
                                                                objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingAllNewPatient")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingExistingPatientsOnly")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                            }

                                                        }

                                                        if (index != 0 && prv.MultipleAddressFound)
                                                        {
                                                            addnewitem = true;
                                                            if (!string.IsNullOrEmpty(response.USPSAddress1))
                                                            {
                                                                objParagraph.Add(new Text(response.USPSAddress1).AddStyle(AHCStyles.Address));
                                                                if (!string.IsNullOrEmpty(response.USPSAddress2))
                                                                    objParagraph.Add(new Text("\u00a0" + response.USPSAddress2).AddStyle(AHCStyles.Address));
                                                                objParagraph.Add("\n");
                                                            }
                                                            objParagraph.Add(new Text(response.USPSCity + ", " + response.USPSState + " " + response.USPSZip5).AddStyle(AHCStyles.Address)).Add("\n");
                                                            if (Helper.ValidPhone(response.Phone))
                                                            {
                                                                objParagraph.Add(new Text(Convert.ToInt64(response.Phone).ToString("(###) ###-####")).AddStyle(AHCStyles.Address));
                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(new Text(response.Phone).AddStyle(AHCStyles.Address));

                                                            }
                                                            objParagraph.Add("\n");

                                                            if (!string.IsNullOrEmpty(string.Join(", ", response.otherLanguages)))

                                                                objParagraph.Add(new Text(string.Join(", ", response.otherLanguages)).AddStyle(AHCStyles.Address)).Add("\n");
                                                        }


                                                        if (index != 0 && (prv.MultipleAddressFound || prv.MultipleProvIdFound))
                                                        {
                                                            addnewitem = true;

                                                            Text network = new Text(response.IPA).AddStyle(AHCStyles.IPAName);
                                                            objParagraph.Add(network).Add("\n")
                                                             .Add(new Text("\u00a0\u00a0Provider ID: " + response.ProvID).AddStyle(AHCStyles.Address));


                                                            if (response.IsAcceptingNewPatients.ToLower().Equals("yes"))
                                                            {
                                                                objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingAllNewPatient")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                            }
                                                            else
                                                            {
                                                                objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingExistingPatientsOnly")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                            }

                                                        }

                                                        if (addnewitem)
                                                        {
                                                            objParagraph.SetMultipliedLeading(1.0f);
                                                            dv.Add(objParagraph);
                                                            dv.SetKeepTogether(true);
                                                            document.Add(dv);
                                                            if (kj == 0)
                                                            {
                                                                Helper.CreateOutline(CountyOutline, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), city.Name.ToUpper());
                                                            }
                                                            kj++;

                                                            var objpvResponse = new ProviderResponse();
                                                            objpvResponse.CategoryName = config.PageHeader;
                                                            objpvResponse.City = city.Name;
                                                            objpvResponse.County = county.Name;
                                                            objpvResponse.SiteHeading = config.SiteHeading;

                                                            objpvResponse.SkipFooter = false;
                                                            objpvResponse.SkipHeader = false;
                                                            objpvResponse.IsIndexPage = false;
                                                            if (dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber().ToString())))
                                                            {
                                                                dicpage[myrender.GetCurrentArea().GetPageNumber().ToString()].Add(objpvResponse);
                                                            }
                                                            else
                                                            {
                                                                dicpage.Add(myrender.GetCurrentArea().GetPageNumber().ToString(), new List<ProviderResponse>() { objpvResponse });
                                                            }


                                                           
                                                            headerEventhandler.skipCity = false;
                                                            headerEventhandler.HeaderText = string.Empty;
                                                            headerEventhandler.CultureResourceSet = cultureResourceSet;
                                                            footerEventHandler.CultureResourceSet = cultureResourceSet;
                                                            headerEventhandler.entry = dicpage;
                                                            footerEventHandler.entry = dicpage;


                                                        }
                                                        i++;
                                                    }


                                                }

                                            }

                                            j++;


                                        }

                                    }
                                    else
                                    {

                                        foreach (HospitalList hpl in city.HospitalList)
                                        {
                                            Text Name;
                                            Text PsychiatricSymbol= new Text("t").AddStyle(AHCStyles.Symbol);
                                            if (cityix != 0)
                                            {
                                                dv = new Div();
                                            }
                                            objParagraph = new Paragraph();
                                            Name = new Text(hpl.ProviderList.FirstOrDefault().NPPESOrganizationName).AddStyle(AHCStyles.ProvName);
                                            if (hpl.ProviderList.FirstOrDefault().IsPsychiatricHospital)
                                            {
                                                objParagraph.Add(PsychiatricSymbol).Add(Name);
                                            }
                                            else
                                            {
                                                objParagraph.Add(Name);
                                            }
                                            objParagraph.Add("\n");
                                            objParagraph.SetDestination(hpl.ProviderList.FirstOrDefault().NPPESOrganizationName);
                                            Dictionary<string, string> addressduplicate = new Dictionary<string, string>();
                                            int ix = 0;
                                            foreach (ProviderResponse response in hpl.ProviderList)
                                            {

                                                string key = response.USPSAddress1 + " " + response.USPSAddress2 + " " + response.USPSCity + ", " +
                                                                     response.USPSState + " " +
                                                                     response.USPSZip5;
                                                if (!addressduplicate.ContainsKey(key))
                                                {
                                                    addressduplicate.Add(key, response.NPPESOrganizationName);

                                                    if (!string.IsNullOrEmpty(response.USPSAddress1))
                                                    {
                                                        if (ix != 0) objParagraph.Add("\n");
                                                        objParagraph.Add(new Text(response.USPSAddress1).AddStyle(AHCStyles.Address));
                                                        if (!string.IsNullOrEmpty(response.USPSAddress2))
                                                            objParagraph.Add(new Text("\u00a0" + response.USPSAddress2).AddStyle(AHCStyles.Address));
                                                        objParagraph.Add("\n");
                                                    }
                                                    objParagraph.Add(new Text(response.USPSCity + ", " +
                                                                         response.USPSState + " " +
                                                                         response.USPSZip5).AddStyle(AHCStyles.Address)).Add("\n");


                                                    if (Helper.ValidPhone(response.Phone))
                                                    {
                                                        objParagraph.Add(new Text(Convert.ToInt64(response.Phone).ToString("(###) ###-####")).AddStyle(AHCStyles.Address));
                                                    }
                                                    else
                                                    {
                                                        objParagraph.Add(new Text(response.Phone).AddStyle(AHCStyles.Address));

                                                    }
                                                    objParagraph.Add("\n");


                                                    if (!string.IsNullOrEmpty(response.NPPESOrganizationName))
                                                    {

                                                        if (pvIndex.ContainsKey(response.NPPESOrganizationName))
                                                        {
                                                            pvIndex[response.NPPESOrganizationName].Add((myrender.GetCurrentArea().GetPageNumber() + 1).ToString());
                                                        }
                                                        else
                                                        {
                                                            pvIndex.Add(response.NPPESOrganizationName, new List<string>() { (myrender.GetCurrentArea().GetPageNumber() + 1).ToString() });

                                                        }
                                                    }
                                                }
                                                ix++;
                                            }

                                            objParagraph.SetMultipliedLeading(1.0f);
                                            dv.Add(objParagraph);
                                            dv.SetKeepTogether(true);
                                            document.Add(dv);


                                            var objPVresponse = new ProviderResponse();
                                            objPVresponse.CategoryName = config.PageHeader;
                                            objPVresponse.City = city.Name;
                                            objPVresponse.County = county.Name;
                                            objPVresponse.SiteHeading = config.SiteHeading;
                                            objPVresponse.SkipDisclaimer = true;

                                            objPVresponse.SkipFooter = false;
                                            objPVresponse.SkipHeader = false;
                                            objPVresponse.IsIndexPage = false;
                                            if (config.ChapterName.ToLower().Equals("hospitals"))
                                            {
                                                objPVresponse.showHospitalDisclaimer = true;
                                            }
                                            if (dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber().ToString())))
                                            {
                                                dicpage[myrender.GetCurrentArea().GetPageNumber().ToString()].Add(objPVresponse);
                                            }
                                            else
                                            {
                                                dicpage.Add(myrender.GetCurrentArea().GetPageNumber().ToString(), new List<ProviderResponse>() { objPVresponse });
                                            }
                                          
                                            headerEventhandler.skipCity = false;
                                            headerEventhandler.HeaderText = string.Empty;
                                            headerEventhandler.entry = dicpage;
                                            footerEventHandler.entry = dicpage;
                                            headerEventhandler.CultureResourceSet = cultureResourceSet;
                                            footerEventHandler.CultureResourceSet = cultureResourceSet;


                                            cityix++;
                                        }

                                    }

                                }
                            }
                            else
                            {
                                //Virtual Provider

                                foreach (var spl in county.SpecialitiesList.Where(a => !string.IsNullOrEmpty(a.Name)))
                                {
                                    if (j != 0)
                                    {
                                        dv = new Div();
                                    }

                                    objParagraph = new Paragraph(spl.Name.ToUpper());
                                    objParagraph.SetFont(AHCFonts.TimesRoman_Bold);
                                    objParagraph.SetMarginBottom(0f);
                                    dv.Add(objParagraph);

                                    SolidLine line = new SolidLine();
                                    line.SetColor(ColorConstants.BLACK);
                                    LineSeparator ls = new LineSeparator(line);
                                    ls.GetAccessibilityProperties().SetRole(StandardRoles.ARTIFACT);
                                    dv.Add(ls);

                                    List<string> lstCity = new List<string>();
                                    int i = 0;
                                    foreach (IPAList prv in spl.IPAList)
                                    {

                                        //currentTitle = prv.Name;//                                 
                                        Dictionary<string, int> entry = new Dictionary<string, int>();
                                        Text symbol = new Text("v").AddStyle(AHCStyles.Symbol);
                                        Text star = new Text("*");//.AddStyle(AHCStyles.Symbol);
                                        Text Name;
                                        if (i != 0)
                                        {
                                            dv = new Div();
                                        }



                                        for (int index = 0; index < prv.ProviderList.Count; index++)
                                        {
                                            bool foundrecords = false;

                                            objParagraph = new Paragraph();
                                            var response = prv.ProviderList[index];
                                            if (!string.IsNullOrEmpty(response.USPSCity) && !lstCity.Contains(response.USPSCity))
                                                lstCity.Add(response.USPSCity);
                                            if (index == 0)
                                            {
                                                foundrecords = true;

                                                if (!string.IsNullOrEmpty(response.FullName))
                                                {
                                                    if (response.visitType.ToLower().Equals("homevisitsonly"))
                                                    {
                                                        if (pvIndex.ContainsKey(response.FullName+star))
                                                        {
                                                            pvIndex[response.FullName+star].Add((myrender.GetCurrentArea().GetPageNumber() + 1).ToString());
                                                        }
                                                        else
                                                        {
                                                            pvIndex.Add(response.FullName+star, new List<string>() { (myrender.GetCurrentArea().GetPageNumber() + 1).ToString() });

                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (pvIndex.ContainsKey(response.FullName))
                                                        {
                                                            pvIndex[response.FullName].Add((myrender.GetCurrentArea().GetPageNumber() + 1).ToString());
                                                        }
                                                        else
                                                        {
                                                            pvIndex.Add(response.FullName, new List<string>() { (myrender.GetCurrentArea().GetPageNumber() + 1).ToString() });

                                                        }
                                                    }

                                                 

                                                }
                                                Name = new Text(response.FullName).AddStyle(AHCStyles.ProvName);
                                                objParagraph.SetDestination(response.FullName);

                                                if (response.IsMedicaidRegistered.ToLower().Equals("yes"))
                                                {
                                                    objParagraph.Add(symbol).Add(Name);
                                                    if (response.visitType.ToLower().Equals("homevisitsonly"))
                                                    {
                                                        objParagraph.Add(star);
                                                    }
                                                    objParagraph.Add("\n");
                                                }
                                                else
                                                {
                                                    objParagraph.Add(Name);
                                                    if (response.visitType.ToLower().Equals("homevisitsonly"))
                                                    {
                                                        objParagraph.Add(star);
                                                    }
                                                    objParagraph.Add("\n");
                                                }

                                                if (Helper.ValidPhone(response.Phone))
                                                {
                                                    objParagraph.Add(new Text(Convert.ToInt64(response.Phone).ToString("(###) ###-####")).AddStyle(AHCStyles.Address));
                                                }
                                                else
                                                {
                                                    objParagraph.Add(new Text(response.Phone).AddStyle(AHCStyles.Address));

                                                }
                                                objParagraph.Add("\n");

                                                if (!string.IsNullOrEmpty(string.Join(", ", response.otherLanguages)))

                                                    objParagraph.Add(new Text(string.Join(", ", response.otherLanguages)).AddStyle(AHCStyles.Address)).Add("\n");

                                                Text network = new Text(response.IPA).AddStyle(AHCStyles.IPAName);
                                                objParagraph.Add(network).Add("\n")
                                                 .Add(new Text("\u00a0\u00a0Provider ID: " + response.ProvID).AddStyle(AHCStyles.Address));

                                                if (response.IsAcceptingNewPatients.ToLower().Equals("yes"))
                                                {
                                                    objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingAllNewPatient")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                }
                                                else
                                                {
                                                    objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingExistingPatientsOnly")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                }

                                            }




                                            if (index != 0 && (prv.MultipleProvIdFound))
                                            {
                                                foundrecords = true;
                                                Text network = new Text(response.IPA).AddStyle(AHCStyles.IPAName);
                                                objParagraph.Add(network).Add("\n")
                                                 .Add(new Text("\u00a0\u00a0Provider ID: " + response.ProvID).AddStyle(AHCStyles.Address));

                                                if (response.IsAcceptingNewPatients.ToLower().Equals("yes"))
                                                {
                                                    objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingAllNewPatient")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                }
                                                else
                                                {
                                                    objParagraph.Add(new Text("\n\u00a0\u00a0" + cultureResourceSet.GetString("AcceptingExistingPatientsOnly")).AddStyle(AHCStyles.AcceptingPatientStyle));

                                                }

                                            }
                                            if (foundrecords)
                                            {
                                                //foundrecords = false;
                                                objParagraph.SetMultipliedLeading(1.0f);
                                                dv.Add(objParagraph);
                                                dv.SetKeepTogether(true);
                                            }

                                        }


                                        document.Add(dv);
                                        var objpvResponse = new ProviderResponse();
                                        objpvResponse.CategoryName = config.PageHeader;
                                        objpvResponse.ChapterOtherLanguage = config.ChapterNameOtherLanguage;
                                        objpvResponse.chaptername = config.ChapterName;
                                        if (lstCity.Count > 1)
                                            lstCity[lstCity.Count - 1] = " and " + lstCity.LastOrDefault();

                                        objpvResponse.City = string.Join(", ", lstCity.ToList());
                                        if (objpvResponse.City.Contains("and"))
                                        {
                                            int index = objpvResponse.City.LastIndexOf(',');
                                            objpvResponse.City = objpvResponse.City.Remove(index, 1);
                                        }
                                        objpvResponse.County = county.Name;
                                        objpvResponse.SiteHeading = config.SiteHeading;

                                        objpvResponse.SkipFooter = false;
                                        objpvResponse.SkipHeader = false;
                                        objpvResponse.IsIndexPage = false;

                                        if (dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber().ToString())))
                                        {
                                            dicpage[myrender.GetCurrentArea().GetPageNumber().ToString()].Add(objpvResponse);
                                        }
                                        else
                                        {
                                            dicpage.Add(myrender.GetCurrentArea().GetPageNumber().ToString(), new List<ProviderResponse>() { objpvResponse });
                                        }
                                       
                                        headerEventhandler.CultureResourceSet = cultureResourceSet;
                                        footerEventHandler.CultureResourceSet = cultureResourceSet;
                                        headerEventhandler.skipCity = true;
                                        headerEventhandler.HeaderText = string.Empty;
                                        headerEventhandler.entry = dicpage;
                                        footerEventHandler.entry = dicpage;

                                        i++;
                                    }
                                    j++;


                                }
                            }


                            countyix++;
                        }

                        #endregion

                    }




                    log.LogInformation("PD-CreatePdf - Section 2 Template Completed");
                    log.LogInformation("PD-CreatePdf - Index Page Started");

                    //Create Index page
                    #region Index Page
                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                    var Oindex = Helper.CreateOutline(ODirectory, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("Index").ToUpper());
                    Oindex.SetOpen(false);
                    //document.Add(new Paragraph("Index"));
                    List<TabStop> tabStops = new List<TabStop>();
                    tabStops.Add(new TabStop(columnWidth, TabAlignment.RIGHT, new DottedLine()));
                    char prev = 'A';
                    int ii = 0;

                    var items = from pair in pvIndex
                                orderby pair.Key ascending
                                select pair;
                    Rectangle rect = new Rectangle(0, 0);


                    int indexStartPageNo = myrender.GetCurrentArea().GetPageNumber();
                    foreach (KeyValuePair<string, List<string>> str in items)
                    {

                        Div divIx = new Div();
                        divIx.GetAccessibilityProperties().SetRole(StandardRoles.TOC);
                        Div divtoci = new Div();
                        divtoci.GetAccessibilityProperties().SetRole(StandardRoles.TOCI);
                        Paragraph index = new Paragraph();
                        // index.SetMultipliedLeading(1.2f);
                        string guidRef = new Guid().ToString();
                        if (ii == 0)
                        {
                            Helper.CreateOutline(Oindex, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), str.Key[0].ToString().ToUpper());
                            prev = str.Key[0];
                            index = new Paragraph(str.Key[0].ToString().ToUpper());
                            index.SetDestination(guidRef + str.Key[0]);
                            // index.GetAccessibilityProperties().SetRole(StandardRoles.P);
                            index.SetFontColor(ColorConstants.WHITE);
                            index.SetBackgroundColor(ColorConstants.BLACK);
                            index.SetTextAlignment(TextAlignment.CENTER);
                            index.SetKeepTogether(true);
                            index.SetPadding(2f);
                            //document.Add(index);
                            divtoci.Add(index);

                        }
                        if (prev != str.Key[0])
                        {
                            Helper.CreateOutline(Oindex, pdf.GetPage(document.GetRenderer().GetCurrentArea().GetPageNumber()), str.Key[0].ToString().ToUpper());
                            // Helper.CreateOutlineName(Oindex, str.Key[0].ToString().ToUpper(), guidRef + str.Key[0]);
                            prev = str.Key[0];
                            index = new Paragraph(str.Key[0].ToString().ToUpper());
                            index.SetDestination(guidRef + str.Key[0]);
                            index.SetKeepTogether(true);
                            //index.GetAccessibilityProperties().SetRole(StandardRoles.P);
                            index.SetFontColor(ColorConstants.WHITE);
                            index.SetBackgroundColor(ColorConstants.BLACK);
                            index.SetTextAlignment(TextAlignment.CENTER);
                            index.SetPadding(2f);
                            // document.Add(index);
                            divtoci.Add(index);


                        }
                        ii++;

                        index = new Paragraph();
                        index.SetPaddingTop(2f);
                        index.SetPaddingBottom(2f);
                        index.SetMargin(0f);
                        // index.SetMultipliedLeading(1.28f);
                        index.GetAccessibilityProperties().SetRole(StandardRoles.LINK);
                        index.SetMarginLeft(10)
                         .AddTabStops(tabStops)
                         .Add(str.Key)
                         .Add(new Tab())
                         .Add(new Text(string.Join(", ", str.Value.Distinct().ToList())))
                         .AddStyle(AHCStyles.Index)
                         .SetKeepTogether(true)
                         .SetAction(PdfAction.CreateGoTo(str.Key));

                        divtoci.SetKeepTogether(true);
                        divtoci.Add(index);
                        divIx.Add(divtoci);

                        document.Add(divIx);
                        if (!dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber()).ToString()))
                        {
                            dicpage.Add((myrender.GetCurrentArea().GetPageNumber()).ToString(), new List<ProviderResponse>() { new ProviderResponse() { Disclaimer = string.Empty, TOCPage = false, IsIndexPage = true, SkipHeader = false, SkipFooter = true } }); ;

                        }
                        footerEventHandler.CultureResourceSet = cultureResourceSet;
                        headerEventhandler.skipCity = false;
                        footerEventHandler.ShowPageNo = false;
                        headerEventhandler.HeaderText = string.Empty;
                        headerEventhandler.entry = dicpage;
                        footerEventHandler.entry = dicpage;


                    }



                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE)); // Add information page
                    if (!dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber()).ToString()))
                    {
                        dicpage.Add((myrender.GetCurrentArea().GetPageNumber()).ToString(), new List<ProviderResponse>() { new ProviderResponse() { Disclaimer = cultureResourceSet.GetString("directoryinfomsg"), ShowPageno = false, TOCPage = false, IsIndexPage = true, SkipHeader = false, SkipFooter = true } });

                        headerEventhandler.entry = dicpage;
                        footerEventHandler.Config = ProvLst.FirstOrDefault().Config;
                        footerEventHandler.entry = dicpage;
                    }


                    #endregion
                    #region TableofContents



                    document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
                    document.SetRenderer(new DocumentRenderer(document));
                    document.Add(new AreaBreak(AreaBreakType.LAST_PAGE));


                    log.LogInformation("PD-CreatePdf - Index Page Completed");
                    log.LogInformation("PD-CreatePdf - TOC Page Started");


                    Helper.CreateTOC(pdf, document, toc, footerEventHandler, cultureResourceSet);
                    if (!dicpage.ContainsKey((myrender.GetCurrentArea().GetPageNumber()).ToString()))
                    {
                        dicpage.Add((myrender.GetCurrentArea().GetPageNumber()).ToString(), new List<ProviderResponse>() { new ProviderResponse() { ShowPageno = true, TOCPage = true, IsIndexPage = false, SkipHeader = false, SkipFooter = true } });
                        headerEventhandler.entry = null;
                        footerEventHandler.entry = dicpage;
                    }

                    Helper.CreateOutline(ODirectory, pdf.GetPage(myrender.GetCurrentArea().GetPageNumber()), cultureResourceSet.GetString("TableOfContents").ToUpper(), 0);
                    log.LogInformation("PD-CreatePdf - TOC Page Completed");
                    #endregion

                    #endregion
                    //}
                    document.Close();



                    var sourceStream = new MemoryStream(stream.ToArray());
                    var destStream = new MemoryStream();
                    var newstream = new MemoryStream();
                    PdfDocument pdfDoc = new PdfDocument(new PdfReader(sourceStream), new PdfWriter(destStream));
                    pdfDoc.MovePage(pdfDoc.GetLastPage(), 2);
                    //  pdfDoc.RemovePage(pdfDoc.GetNumberOfPages());
                    //PdfMerger merger = new PdfMerger(pdfDoc);
                    //var memorys = new MemoryStream(CreateLawPage(Language));
                    //PdfDocument pdflawDoc = new PdfDocument(new PdfReader(memorys));
                    //merger.Merge(pdflawDoc, 1, 1);                       

                    pdfDoc.Close();

                    var ss = new MemoryStream(destStream.ToArray());
                    //LicenseKey.LoadLicenseFile(functionAppDirectory + "/Config/AlignmentKey2021.xml");
                    PdfOptimizer optimizer = new PdfOptimizer();
                    optimizer.AddOptimizationHandler(new FontDuplicationOptimizer());
                    optimizer.AddOptimizationHandler(new CompressionOptimizer());

                    ImageQualityOptimizer jpeg_optimizer = new ImageQualityOptimizer();
                    jpeg_optimizer.SetJpegProcessor(new JpegCompressor(.5f));
                    optimizer.AddOptimizationHandler(jpeg_optimizer);
                    //result.Language = ProvLst.FirstOrDefault()?.Config?.Lanaguage;
                    optimizer.Optimize(ss, newstream);
                    result.PdfStream = newstream.ToArray();
                }
                else
                {
                    log.LogInformation("Issue on fetching resource file for " + Language + " language");
                }

            }
            catch (Exception ex)
            {
                log.LogInformation(ex.StackTrace);
                result.Failed = true;
                result.ErrorMessage = ex.StackTrace;
                log.LogError(ex.StackTrace);
            }

            return result;
        }


        /// <summary>
        /// updatePendingMemberCountActivity.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="log"></param>
        /// <param name="Econtext"></param>
        /// <returns></returns>
        [FunctionName("UpdateCountyListActivity")]
        public async Task<bool> UpdateCountyListActivity([ActivityTrigger] IDurableActivityContext context,
            ILogger log, ExecutionContext Econtext)
        {
            try
            {
                RequestParameter param = context.GetInput<RequestParameter>();
                var sqlClient = new EdwSqlClient(param.aPIContext);
                var dapperContext = new DapperContext(param.aPIContext, sqlClient);
                StorageTableOperation<PDConfigurationManager> tableObj = new StorageTableOperation<PDConfigurationManager>("PDConfig", param.aPIContext.AzureWebJobsStorage);
                PDConfigurationManager pdconfig = tableObj.GetTableData("ConfigValue", param.Version.ToString());
                string LastRunDate = pdconfig?.LastRunDate;
                if (string.IsNullOrEmpty(LastRunDate) || param.aPIContext.ForcetoRestart == "1")
                {
                    pdconfig = new PDConfigurationManager("ConfigValue", param.Version.ToString());
                    pdconfig.LastRunDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    await tableObj.UpdateTableData("LastRunDate", param.Version.ToString(), pdconfig);
                    LastRunDate = pdconfig.LastRunDate;
                }

                int pendingCount = 0; // await dapperContext.GetTPDFListCount(Convert.ToDecimal(param.Version.ToString(), CultureInfo.InvariantCulture), LastRunDate);//
                int totalMemberscount = 0; // await dapperContext.GetTotalMemberCount();//
                if (totalMemberscount == pendingCount || pendingCount == 0)
                {
                    pdconfig.LastRunDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    //pdconfig.PendingMemberCount = pendingCount.ToString();//
                    await tableObj.UpdateTableData("LastRunDate", param.Version.ToString(), pdconfig);
                    LastRunDate = pdconfig.LastRunDate;
                    log.LogInformation("Last Run Date " + param.Version.ToString() + " " + LastRunDate);
                }
                else
                {
                    pdconfig.LastRunDate = LastRunDate;
                    //pdconfig.PendingMemberCount = pendingCount.ToString();//
                    await tableObj.UpdateTableData("LastRunDate", param.Version.ToString(), pdconfig);
                }
            }
            catch (Exception ex)
            {
                log.LogInformation("updatePendingMemberCountActivity" + ex.Message);
            }
            return true;
        }

        private static DataLakeDirectoryClient GetADLSDirectClient(string fileSystemName, RequestParameter param, string filepath, ILogger log)
        {
            try
            {
                StorageSharedKeyCredential sharedKeyCredential = new StorageSharedKeyCredential(param.aPIContext.ADLSAccountName,
                param.aPIContext.ADLSAccountKey);
                DataLakeServiceClient serviceClient = new DataLakeServiceClient(new Uri(param.aPIContext.ADLSServiceURI), sharedKeyCredential);
                var x = serviceClient.GetFileSystems(Azure.Storage.Files.DataLake.Models.FileSystemTraits.None)?.Where(a => a.Name == fileSystemName)?.ToList();
                DataLakeFileSystemClient fileSystemClient = serviceClient.GetFileSystemClient(fileSystemName);

                if (x == null || x.Count == 0)
                {
                    fileSystemClient = serviceClient.CreateFileSystem(fileSystemName);
                }

                DataLakeDirectoryClient directoryClient = null;

                directoryClient = fileSystemClient.CreateDirectory(filepath);

                return directoryClient;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public class Outlines
        {
            List<Outline> lstOutline = new List<Outline>();
        }
        public class Outline
        {
            public string Title { get; set; }
            public string PageNo { get; set; }
        }
    }
}