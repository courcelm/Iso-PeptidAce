﻿/*
 * Copyright 2013 Olivier Caron-Lizotte
 * olivierlizotte@gmail.com
 * Licensed under the MIT license: <http://www.opensource.org/licenses/mit-license.php>
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PeptidAce.Utilities;
using PeptidAce.Utilities.Methods;
using PeptidAce.Iso.Structures;
using PeptidAce.Utilities.Interfaces;

namespace PeptidAce.Iso.Methods
{
    /// <summary>
    /// Contains Isomeric Peptide Deconvolution methods. Dependant on PeptidAce library (which in turn depends on the 
    /// original implementation of Morpheus
    /// TODO : Replace Morpheus sources with up to date CSMSL library
    /// </summary>
    public class PositionnalIsomerSolver
    {
        //Number of fragment to consider (per isomer)
        public int nbMinFragments = 5;
        public int nbMaxFragments = 5;
        //Tolerance windows
        public double precTolPpm = 8;
        public double prodTolPpm = 20;

        public long precision     = 1000;
        private DBOptions dbOptions;
        
        public Samples SpikedSamples;
        private Result  SpikedResult;

        public Samples MixedSamples;
        private Result  mixedResult;

        //Synthetic peptides
        public Dictionary<double, Dictionary<Sample, CharacterizedPrecursor>> characterizedPeptides;
        //Mixed spectrum to deconvolute
        public Dictionary<Sample, List<MixedPrecursor>>         mixedPrecursors;

        public string OutputFolder { get { return dbOptions.OutputFolder + "Combined" + System.IO.Path.DirectorySeparatorChar;  } }

        /// <summary>
        /// Creates the options used through the system for peptide identification
        /// </summary>
        /// <param name="fastaFile"></param>
        /// <param name="outputFolder"></param>
        /// <param name="consol"></param>
        /// <returns></returns>
        private DBOptions CreateOptions(string fastaFile, string outputFolder, IConSol consol)
        {
            DBOptions dbOptions = new DBOptions(fastaFile, consol);
            dbOptions.precursorMassTolerance = new MassTolerance(precTolPpm, MassToleranceUnits.ppm);
            dbOptions.productMassTolerance = new MassTolerance(prodTolPpm, MassToleranceUnits.ppm);
            dbOptions.MaximumPeptideMass = 200000;
            dbOptions.OutputFolder = outputFolder;

            ProteaseDictionary proteases = ProteaseDictionary.Instance;
            dbOptions.DigestionEnzyme = proteases["no enzyme"];
            //dbOptions.DigestionEnzyme = proteases["top-down"];
            dbOptions.NoEnzymeSearch = false;
            dbOptions.DecoyFusion = false;
            dbOptions.MaximumNumberOfFragmentsPerSpectrum = 400;
            dbOptions.ToleratedMissedCleavages = 200;
            dbOptions.MinimumPeptideLength = 5;
            dbOptions.MaximumPeptideLength = 300;

            List<Modification> fixMods = new List<Modification>();
            dbOptions.fixedModifications = fixMods;

            List<Modification> varMods = new List<Modification>();
            foreach (string strMod in ModificationDictionary.Instance.Keys)
                varMods.Add(ModificationDictionary.Instance[strMod]);

            dbOptions.maximumVariableModificationIsoforms = 1024;
            dbOptions.variableModifications = varMods;

            dbOptions.addFragmentLoss = false;
            dbOptions.addFragmentMods = false;//Gives very bad results... might be off
            
            dbOptions.SaveMS1Peaks = true;
            dbOptions.SaveMSMSPeaks = true;
            dbOptions.LoadSpectraIfFound = true;

            dbOptions.NbPSMToKeep = 100;
            return dbOptions;
        }

        /// <summary>
        /// Provides deconvoluted elution curves of mixed spectra from the provided raw files using the provided synthetic raw file
        /// Exports in CSV files and stores everything in class objects
        /// </summary>
        /// <param name="spikedRaws"></param>
        /// <param name="mixedRaws"></param>
        /// <param name="fastaFile"></param>
        /// <param name="folderToOutputTo"></param>
        /// <param name="conSol"></param>
        public void Solve(string[] spikedRaws, string[] mixedRaws, string fastaFile, string folderToOutputTo, IConSol conSol)
        {
            dbOptions = CreateOptions(fastaFile, folderToOutputTo, conSol);
            SpikedSamples = new Samples(dbOptions);
            for (int i = 0; i < spikedRaws.Length; i++)
                SpikedSamples.Add(new Sample(i + 1, 1, 1, spikedRaws[i], spikedRaws[i], 0, ""));

            //Precompute Spiked peptide identifications
            SpikedResult = Ace.Start(dbOptions, SpikedSamples, false, false);
            SpikedResult.ExportPSMs(1, dbOptions.OutputFolder + "Identifications" + System.IO.Path.DirectorySeparatorChar + "SpikedSamplesPSMs.csv");

            MixedSamples = new Samples(dbOptions);
            for (int i = 0; i < mixedRaws.Length; i++)
                MixedSamples.Add(new Sample(i + 1, 1, 1, mixedRaws[i], mixedRaws[i], 0, ""));

            //Precompute Mixed peptide identifications
            mixedResult = Ace.Start(dbOptions, MixedSamples, false, false);
            mixedResult.ExportPSMs(1, dbOptions.OutputFolder + "Identifications" + System.IO.Path.DirectorySeparatorChar + "MixedSamplesPSMs.csv");

            conSol.WriteLine("Computing gradient descents...");

            //Compute all usable spiked peptides
            characterizedPeptides = CharacterizedPrecursor.GetSpikedPrecursors(SpikedSamples, SpikedResult, dbOptions, nbMinFragments, nbMaxFragments);
            ExportSpikedSampleResult(characterizedPeptides, dbOptions);

            vsCSVWriter writerCumul = new vsCSVWriter(OutputFolder + "Results.csv");
            string titleCombined = "Mixed Sample,Precursor";            
            string curveStr = "Polynomial Curve,";
            string spikedIntensityStr = "Area under the curve,";
            foreach(double precursor in characterizedPeptides.Keys)
                foreach (CharacterizedPrecursor charPrec in characterizedPeptides[precursor].Values)
                {
                    titleCombined += "," + charPrec.Peptide.Sequence + " Charge " + charPrec.Charge;

                    if (charPrec.eCurve.Coefficients != null && charPrec.eCurve.Coefficients.Length == 3)
                        curveStr += "," + charPrec.eCurve.Coefficients[0] + "x^2 + " + charPrec.eCurve.Coefficients[1] + "x" + charPrec.eCurve.Coefficients[2];
                    else
                        curveStr += ",NA";

                    spikedIntensityStr += "," + charPrec.eCurve.Area;
                }
            writerCumul.AddLine(titleCombined);
            writerCumul.AddLine(curveStr);
            writerCumul.AddLine(spikedIntensityStr);

            //mixedPrecursors = new Dictionary<Sample, Dictionary<double, MixedPrecursor>>();
            mixedPrecursors = new Dictionary<Sample, List<MixedPrecursor>>();

            foreach (Sample mixedSample in MixedSamples) 
                mixedPrecursors.Add(mixedSample, MixedPrecursor.GetMixedPrecursors(mixedSample, mixedResult, dbOptions, characterizedPeptides));

            //Get the list of precursors to characterize
            foreach (Sample mixedSample in MixedSamples)
            {
                foreach (double keyMz in characterizedPeptides.Keys)
                {
                    List<Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>> listOfRatios = new List<Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>>();
                    foreach(MixedPrecursor mPrec in mixedPrecursors[mixedSample])
                        if(mPrec.MZ == keyMz)
                        {
                            // Compute Max Flow for this precursor
                            Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> ratios = GetRatios(characterizedPeptides, mPrec);
                            listOfRatios.Add(ratios);

                            ExportMixedSampleResult(ratios, mixedSample, mPrec, keyMz, dbOptions);
                        }

                    bool isEmpty = true;
                    string resultStr = vsCSV.GetFileName(mixedSample.sSDF) + "," + keyMz;
                    foreach (double precursor in characterizedPeptides.Keys)
                    {
                        foreach (CharacterizedPrecursor charPrec in characterizedPeptides[precursor].Values)
                        {
                            double cumulArea = 0.0;
                            foreach (Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> ratios in listOfRatios)
                                if (ratios.ContainsKey(charPrec))
                                    cumulArea += ratios[charPrec].eCurvePerMs.Area;
                            resultStr += "," + cumulArea;
                            if (cumulArea > 0)
                                isEmpty = false;
                        }
                    }
                    if(!isEmpty)
                        writerCumul.AddLine(resultStr);
                }
            }
            writerCumul.WriteToFile();

            //List Modifications
            Dictionary<Modification, double> dicOfIntensityPerMod = new Dictionary<Modification,double>();
            foreach(Sample sample in mixedPrecursors.Keys)
                foreach(MixedPrecursor mP in mixedPrecursors[sample])
                    foreach(CharacterizedPrecursor cP in mP.PeptideRatios.Keys)
                        if(cP.Peptide.VariableModifications != null)
                            foreach(Modification mod in cP.Peptide.VariableModifications.Values)
                                if(!dicOfIntensityPerMod.ContainsKey(mod))
                                    dicOfIntensityPerMod.Add(mod, 0.0);


            //Compute site occupancy for identical sequences (real positionnal isomers)
            vsCSVWriter writerSitesOccupancy = new vsCSVWriter(OutputFolder + "Results_SiteOccupancy.csv");
            List<Protein> AllProteins = Ace.ReadProteomeFromFasta(fastaFile, false, dbOptions);
            foreach(Protein protein in AllProteins)
            { 
                string newTitleProtein = protein.Description.Replace(',', ' ') + "," + protein.Sequence;
                for(int i = 0; i < protein.Sequence.Length; i++)
                    newTitleProtein += "," + protein[i].ToString();
                writerSitesOccupancy.AddLine(newTitleProtein);

                foreach (Sample mixedSample in mixedPrecursors.Keys)
                {                      
                    string coverage = "Coverage," + mixedSample.Name;
                    for(int i = 0; i < protein.Sequence.Length; i++)
                    {
                        double cumulSite = 0.0;
                        newTitleProtein += "," + protein[i].ToString();                    
                        foreach(MixedPrecursor mP in mixedPrecursors[mixedSample])
                        {
                            foreach(CharacterizedPrecursor cP in mP.PeptideRatios.Keys)
                            {
                                if(i + 1 >= cP.Peptide.StartResidueNumber && i + 1 <= cP.Peptide.EndResidueNumber)
                                    cumulSite += mP.PeptideRatios[cP].eCurvePerMs.Area;
                            }
                        }
                        coverage += "," + cumulSite;
                    }
                    writerSitesOccupancy.AddLine(coverage);
                }
                
                foreach(Modification mod in dicOfIntensityPerMod.Keys)
                {
                    Dictionary<Sample, string> dicOfLines = new Dictionary<Sample,string>();
                    for(int i = 0; i < protein.Sequence.Length; i++)
                    {                
                        foreach (Sample mixedSample in mixedPrecursors.Keys)
                        {   
                            double cumulModArea = 0.0;
                            foreach(MixedPrecursor mP in mixedPrecursors[mixedSample])
                            {
                                foreach(CharacterizedPrecursor cP in mP.PeptideRatios.Keys)
                                {
                                    if(i + 1 >= cP.Peptide.StartResidueNumber && i + 1 <= cP.Peptide.EndResidueNumber &&
                                        cP.Peptide.VariableModifications != null)
                                    {
                                        foreach(int pos in cP.Peptide.VariableModifications.Keys)
                                            if(cP.Peptide.StartResidueNumber + pos - 2 == i + 1 && cP.Peptide.VariableModifications[pos] == mod)
                                                cumulModArea += mP.PeptideRatios[cP].eCurvePerMs.Area;
                                    }
                                }
                            }
                            if(!dicOfLines.ContainsKey(mixedSample))
                                dicOfLines.Add(mixedSample, mod.Description + "," + mixedSample.Name + "," + cumulModArea);
                            else
                                dicOfLines[mixedSample] += "," + cumulModArea;
                        }
                    }
                    foreach(string line in dicOfLines.Values)
                        writerSitesOccupancy.AddLine(line);
                }
            }
            writerSitesOccupancy.WriteToFile();
        }

        /// <summary>
        /// Exports a CSV file with the area under the curve of individual isomers found in the mixed spectras
        /// </summary>
        /// <param name="ratios"></param>
        /// <param name="mixedSample"></param>
        /// <param name="mixedPrecursor"></param>
        /// <param name="keyMz"></param>
        /// <param name="dbOptions"></param>
        private static void ExportMixedSampleResult(Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> ratios, Sample mixedSample, MixedPrecursor mixedPrecursor, double keyMz, DBOptions dbOptions)
        {
            // Export results in a file
            vsCSVWriter writerRatio = new vsCSVWriter(dbOptions.OutputFolder + @"Individual\" + vsCSV.GetFileName_NoExtension(mixedSample.sSDF) + "_" + keyMz + "MZ_" + mixedPrecursor.Queries[0].spectrum.RetentionTimeInMin + "min.csv");
            string titleIndividual = "Scan time,Total Area,Intensity per milliseconds";
            foreach (CharacterizedPrecursor charPep in ratios.Keys)
                titleIndividual += "," + charPep.Peptide.Sequence;
            titleIndividual += ",Intensity Counts";
            foreach (CharacterizedPrecursor charPep in ratios.Keys)
                titleIndividual += "," + charPep.Peptide.Sequence;
            writerRatio.AddLine(titleIndividual);

            string line = "Total," + mixedPrecursor.eCurve.Area + ",";
            foreach (CharacterizedPrecursor charPep in ratios.Keys)
                line += "," + ratios[charPep].eCurvePerMs.Area;
            line += ",";
            foreach (CharacterizedPrecursor charPep in ratios.Keys)
                line += "," + ratios[charPep].eCurveCount.Area;
            writerRatio.AddLine(line);

            for (int i = 0; i < mixedPrecursor.eCurve.intensityCount.Count; i++)
            {
                line = mixedPrecursor.eCurve.time[i] / (1000.0 * 60.0) + "," + mixedPrecursor.eCurve.intensityCount[i];
                foreach (CharacterizedPrecursor charPep in ratios.Keys)
                    line += "," + ratios[charPep].eCurvePerMs.InterpolateIntensity(mixedPrecursor.eCurve.time[i]);
                foreach (CharacterizedPrecursor charPep in ratios.Keys)
                    line += "," + ratios[charPep].eCurveCount.InterpolateIntensity(mixedPrecursor.eCurve.time[i]);
                writerRatio.AddLine(line);
            }
            writerRatio.WriteToFile();
        }

        /// <summary>
        /// Export CSV files of synthetic peptide runs
        /// </summary>
        /// <param name="characterizedPeptides"></param>
        /// <param name="dbOptions"></param>
        private static void ExportSpikedSampleResult(Dictionary<double, Dictionary<Sample, CharacterizedPrecursor>> characterizedPeptides, DBOptions dbOptions)
        {
            foreach (double keyMz in characterizedPeptides.Keys)
            {
                foreach (Sample sample in characterizedPeptides[keyMz].Keys)
                {
                    vsCSVWriter writerRatio = new vsCSVWriter(dbOptions.OutputFolder + @"Individual\" + vsCSV.GetFileName_NoExtension(sample.sSDF) + "_" + keyMz + "MZ.csv");
                    string titleIndividual = "Scan time,Precursor Intensity,Intensity Per Millisecond";
                    foreach (ProductMatch pm in characterizedPeptides[keyMz][sample].AllFragments)
                        titleIndividual += "," + pm.Fragment.Name + pm.fragmentPos + "^" + pm.charge;
                    writerRatio.AddLine(titleIndividual);

                    foreach (Query query in characterizedPeptides[keyMz][sample].Queries)
                    {
                        string line = query.spectrum.RetentionTimeInMin + "," + query.spectrum.PrecursorIntensity + "," + query.spectrum.PrecursorIntensityPerMilliSecond;
                        foreach (ProductMatch pm in characterizedPeptides[keyMz][sample].AllFragments)
                        {
                            double intensity = 0.0;
                            foreach (ProductMatch pmSpec in query.psms[0].AllProductMatches)
                                if (pmSpec.charge == pm.charge && pmSpec.Fragment == pm.Fragment && pmSpec.fragmentPos == pm.fragmentPos)
                                    intensity = pmSpec.obsIntensity;
                            line += "," + intensity;
                        }
                        writerRatio.AddLine(line);
                    }
                    writerRatio.WriteToFile();
                }
            }
        }

        /// <summary>
        /// Retrieves the ratio of isomers within a bunch of queries (precursor elution curve )
        /// </summary>
        /// <param name="spikes"></param>
        /// <param name="mixedPrecursor"></param>
        /// <returns></returns>
        private Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> GetRatios(Dictionary<double, Dictionary<Sample, CharacterizedPrecursor>> spikes, MixedPrecursor mixedPrecursor)        
        {
            Dictionary<Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>, double> DicOfCurveErrors = new Dictionary<Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>, double>();
            
            for (int nbProductsToKeep = nbMinFragments; nbProductsToKeep <= nbMaxFragments; nbProductsToKeep++)
            {
                bool validProducts = true;
                int nbIgnoredSpectrum = 0;
                List<CharacterizedPrecursor> Isomers = new List<CharacterizedPrecursor>();
                foreach (double mz in spikes.Keys)
                    if (Math.Abs(Utilities.Numerics.CalculateMassError(mz, mixedPrecursor.MZ, dbOptions.precursorMassTolerance.Units)) <= dbOptions.precursorMassTolerance.Value)
                        foreach (Sample sample in spikes[mz].Keys)
                            if (spikes[mz][sample].IsValid(nbProductsToKeep))
                                Isomers.Add(spikes[mz][sample]);
                            else
                                validProducts = false;
                if (validProducts)
                {
                    double cumulError = 0;
                    Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> curves = new Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>();
                    foreach (Query query in mixedPrecursor.Queries)
                    {
                        double timeInMilliSeconds = query.spectrum.RetentionTimeInMin * 60.0 * 1000.0;
  //                      double overFlow = 0;
                        double underFlow = 0;
                        double percentError = 0;
                        Dictionary<CharacterizedPrecursor, SolvedResult> finalRatios = SolveFromSpectrum(Isomers, nbProductsToKeep, query.spectrum.Peaks, dbOptions.productMassTolerance,
                                                                mixedPrecursor.eCurveIntensityPerMS.GetLocalArea(timeInMilliSeconds, timeInMilliSeconds + query.spectrum.InjectionTime),//query.spectrum.PrecursorIntensityPerMilliSecond * query.spectrum.InjectionTime, 
                                                                query.spectrum.PrecursorIntensity,
                                                                out underFlow, out percentError, dbOptions.ConSole);

                        cumulError += underFlow;// percentError;
                        if (percentError < 0.5)
                        {
                            foreach (CharacterizedPrecursor cPep in finalRatios.Keys)
                            {
                                if (!curves.ContainsKey(cPep))
                                    curves.Add(cPep, new MaxFlowElutionCurve(nbProductsToKeep));

                                //curves[cPep].AddPoint(timeInMilliSeconds, finalRatios[cPep].Ratio * query.spectrum.PrecursorIntensityPerMilliSecond);                                
                                //curves[cPep].eCurvePerMs.AddPoint(timeInMilliSeconds, finalRatios[cPep].Ratio * mixedPrecursor.eCurveIntensityPerMS.InterpolateIntensity(timeInMilliSeconds));
                                curves[cPep].eCurvePerMs.AddPoint(timeInMilliSeconds, finalRatios[cPep].NbFitTimes / query.spectrum.InjectionTime);
                                curves[cPep].eCurveCount.AddPoint(timeInMilliSeconds, finalRatios[cPep].Ratio * mixedPrecursor.eCurve.InterpolateIntensity(timeInMilliSeconds));
                            }
                        }
                        else
                            nbIgnoredSpectrum++;

                        if (nbIgnoredSpectrum * 2 > mixedPrecursor.Queries.Count)
                            break;
                    }//End of foreach query

                    if (nbIgnoredSpectrum * 2 < mixedPrecursor.Queries.Count)
                    {
                        if (nbIgnoredSpectrum > 0)
                            Console.WriteLine("Ignored Spectrum : " + nbIgnoredSpectrum + " / " + mixedPrecursor.Queries.Count);

                        foreach (CharacterizedPrecursor cPep in curves.Keys)
                            curves[cPep].Compute();

                        Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> curvesToKeep = new Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve>();
                        foreach (CharacterizedPrecursor cPep in curves.Keys)
                            if (curves[cPep].eCurvePerMs.Area > 0)
                                curvesToKeep.Add(cPep, curves[cPep]);

                        if (curvesToKeep.Count > 0)
                            DicOfCurveErrors.Add(curvesToKeep, cumulError);
                    }
                }
            }//End of for each nbProduct            

            Dictionary<CharacterizedPrecursor, MaxFlowElutionCurve> averagedValues = mixedPrecursor.ComputePeptideRatios(DicOfCurveErrors);
            return averagedValues;
        }
        
        /// <summary>
        /// Small class holding results of a single Gradient Descent run
        /// </summary>
        public class SolvedResult
        {
            public double Ratio;
            public double NbFitTimes;
            public SolvedResult(double ratio, double nbTimes)
            {
                this.Ratio = ratio;
                this.NbFitTimes = nbTimes;
            }
        }

        /// <summary>
        /// Extract isomer ratios from a given spectrum (transformed into capacity vector)
        /// </summary>
        /// <param name="ratiosToFit"></param>
        /// <param name="nbProductsToKeep"></param>
        /// <param name="capacity"></param>
        /// <param name="tolerance"></param>
        /// <param name="PrecursorIntensityInCTrap"></param>
        /// <param name="PrecursorIntensity"></param>
        /// <param name="underFlow"></param>
        /// <param name="percentError"></param>
        /// <param name="ConSole"></param>
        /// <returns></returns>
        public static Dictionary<CharacterizedPrecursor, SolvedResult> SolveFromSpectrum(IEnumerable<CharacterizedPrecursor> ratiosToFit, int nbProductsToKeep, 
                                            IEnumerable<MsMsPeak> capacity, MassTolerance tolerance, 
                                            double PrecursorIntensityInCTrap, double PrecursorIntensity,
                                            out double underFlow, out double percentError, IConSol ConSole)
        {
            bool keepGoing = true;
            Dictionary<double, double> mixedSpectrum = new Dictionary<double, double>();
            List<Dictionary<double, double>> unitSpectrum = new List<Dictionary<double, double>>();
            foreach (CharacterizedPrecursor isomer in ratiosToFit)
            {
                foreach (double key in isomer.NormalizedFragments[nbProductsToKeep].Keys)
                    if (!mixedSpectrum.ContainsKey(key))
                    {
                        double cumulIntensity = 0.0;
                        foreach (MsMsPeak peak in capacity)
                            if (Math.Abs(Utilities.Numerics.CalculateMassError(peak.MZ, key, tolerance.Units)) <= tolerance.Value)
                                cumulIntensity += peak.Intensity;

                        mixedSpectrum.Add(key, cumulIntensity);// / PrecursorIntensityInCTrap);
                    }
                if (isomer.NormalizedFragments.ContainsKey(nbProductsToKeep))
                {
                    if (isomer.FragmentNormalizor.ContainsKey(nbProductsToKeep))
                    {
                        Dictionary<double, double> dic = new Dictionary<double, double>();
                        foreach (double key in isomer.NormalizedFragments[nbProductsToKeep].Keys)
                            dic.Add(key, isomer.NormalizedFragments[nbProductsToKeep][key] * 
                                isomer.FragmentNormalizor[nbProductsToKeep].InterpolateIntensity(PrecursorIntensityInCTrap));
                        unitSpectrum.Add(dic);
                    }
                    else
                        unitSpectrum.Add(isomer.NormalizedFragments[nbProductsToKeep]);
                }
                else
                    keepGoing = false;
            }

            //This nbProduct seems relevant, try to use isomer to get ratios for this spectrum
            if (keepGoing)
            {
                List<double> solution = new List<double>();
                double stepSize = PrecursorIntensityInCTrap / 1000.0;
                if (stepSize < 1)
                    stepSize = 1;
                double tmpUnderflow = 0;
                Utilities.Methods.GradientDescent.SolveMaxFlowStyle(unitSpectrum, mixedSpectrum, out solution, out tmpUnderflow, ConSole, stepSize);
                //Utilities.Methods.GradientDescent.SolveFromGradientDescent(unitSpectrum, mixedSpectrum, PrecursorIntensityInCTrap, out solution, out tmpUnderflow, ConSole);

                double sumOfIntensities = 0;
                foreach (double val in mixedSpectrum.Values)
                    sumOfIntensities += val;

                underFlow = tmpUnderflow;
                List<SolvedResult> result = GetResultList(solution, underFlow, sumOfIntensities);

                Dictionary<CharacterizedPrecursor, SolvedResult> resultPerSample = new Dictionary<CharacterizedPrecursor, SolvedResult>();
                int i = 0;
                foreach (CharacterizedPrecursor key in ratiosToFit)
                {
                    resultPerSample.Add(key, result[i]);
                    i++;
                }

                percentError = (underFlow / sumOfIntensities);
                return resultPerSample;
            }
            else
            {
                percentError = 1.0;
                underFlow = 0;
                return new Dictionary<CharacterizedPrecursor, SolvedResult>();
            }
        }
        /// <summary>
        /// Computes ratios out of the number of times each isomer was seen
        /// </summary>
        /// <param name="solution"></param>
        /// <param name="underFlow"></param>
        /// <param name="sumOfIntensities"></param>
        /// <returns></returns>
        private static List<SolvedResult> GetResultList(List<double> solution, double underFlow, double sumOfIntensities)
        {
            List<SolvedResult> rez = new List<SolvedResult>();
            double sumVal = 0.0;
            foreach(double val in solution)
                sumVal += val;
            
            sumVal += (underFlow / sumOfIntensities);//Counter intuitive, but according to test samples, it is less precise

            foreach (double val in solution)
                rez.Add(new SolvedResult(val / sumVal, val));

            return rez;
        }
    }
}